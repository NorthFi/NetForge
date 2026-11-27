// root/selfhost/src/NetForge.Api/NetworkCompliance.cs
// Evaluate stored evidence at read time so stale snapshots cannot remain green.
// Compare only configuration fields, preserving duplicates and ignoring runtime
// status and dynamic routes/addresses. No remediation or remote writes.
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace NetForge.Api;

public sealed record CompliancePolicyInput(string? ExpectedVersion,Guid? BackupProfileId,
    int BackupMaxHours,int SnapshotMaxHours,bool RequireBaseline,int PollIntervalMinutes,bool Confirm);
public sealed record ComplianceBaselineInput(string Reason,bool Confirm,string? SnapshotSha256=null);
internal sealed record ComplianceCheck(string Name,string State,string Detail);

public static class NetworkCompliance
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher");
    private static IResult Denied()=>Results.StatusCode(403);
    private static IResult Invalid(string error)=>Results.BadRequest(new{error});
    private static IResult Conflict(string error)=>Results.Conflict(new{error});
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    // Only these fields form the baseline. Do not treat CPU load, uptime,
    // link running status, dynamically assigned addresses/routes as config.
    private static readonly (string Name,string[] Fields)[] Rules=[
        ("identity",["name"]),
        ("interfaces",["name","type","mtu","disabled"]),
        ("vlans",["name","interface","vlan-id","disabled"]),
        ("bridges",["name","protocol-mode","vlan-filtering","disabled"]),
        ("addresses",["address","network","interface","disabled"]),
        ("routes",["dst-address","gateway","routing-table","distance","disabled"]),
        ("pppoe",["service-name","interface","default-profile","disabled"]),
        ("dhcp",["name","interface","address-pool","disabled"])
    ];
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static JsonElement? Section(JsonElement root,string name)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty(name,out var part)||
            part.ValueKind!=JsonValueKind.Object||!part.TryGetProperty("status",out var status)||
            status.ValueKind!=JsonValueKind.String||status.GetString()!="ok"||
            !part.TryGetProperty("rows",out var rows)||rows.ValueKind!=JsonValueKind.Array)return null;
        return rows;
    }
    private static string? StringField(JsonElement row,string key)=>
        row.ValueKind==JsonValueKind.Object&&row.TryGetProperty(key,out var field)&&
        field.ValueKind==JsonValueKind.String?field.GetString():null;
    private static SortedDictionary<string,string[]> Normalize(JsonElement root)
    {
        var output=new SortedDictionary<string,string[]>(StringComparer.Ordinal);
        foreach(var rule in Rules)
        {
            var rows=Section(root,rule.Name);if(rows is null)continue;
            var values=new List<string>();var valid=true;
            foreach(var row in rows.Value.EnumerateArray())
            {
                if(row.ValueKind!=JsonValueKind.Object){valid=false;break;}
                if(rule.Name is "addresses" or "routes")
                {
                    var dynamic=StringField(row,"dynamic");
                    if(dynamic=="true")continue;
                    if(dynamic!="false"){valid=false;break;}
                }
                // All required fields must be present. An incomplete response
                // cannot silently become an approved empty baseline.
                var fields=new SortedDictionary<string,string>(StringComparer.Ordinal);
                foreach(var key in rule.Fields)
                {
                    var value=StringField(row,key);
                    if(value is null){valid=false;break;}
                    fields[key]=value;
                }
                if(!valid)break;
                values.Add(JsonSerializer.Serialize(fields,Json));
            }
            if(valid&&(rule.Name!="identity"||values.Count==1))
                output[rule.Name]=values.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        return output;
    }
    private static string? Firmware(JsonElement root)
    {
        var rows=Section(root,"resource");
        if(rows is null||rows.Value.GetArrayLength()!=1)return null;
        var raw=StringField(rows.Value[0],"version");
        return raw?.Split(' ',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }
    private static List<ComplianceCheck> Evaluate(Dictionary<string,object?> row,DateTime now)
    {
        var checks=new List<ComplianceCheck>();
        var last=row["last_poll_at"] as DateTime?;
        var fresh=last.HasValue&&last.Value<=now&&last.Value>=now.AddHours(-(int)row["snapshot_max_hours"]!);
        var valid=row["enabled"] is true&&row["address"] as string==row["current_address"] as string&&fresh&&
            row["last_status"] as string is ("ok" or "partial")&&row["snapshot"] is string;
        checks.Add(new("inspection",valid?"pass":"unknown",valid?"Recent inspection evidence":"Disabled, changed address, stale, failed or missing inspection"));
        using var snapshot=JsonDocument.Parse(valid?(string)row["snapshot"]!:"{}");
        if(row["expected_version"] is string expected)
        {
            var actual=Firmware(snapshot.RootElement);
            checks.Add(new("routeros_version",actual is null?"unknown":actual==expected?"pass":"fail",
                actual is null?"No fresh version evidence":$"Expected {expected}; observed {actual}"));
        }
        if(row["backup_profile_id"] is Guid)
        {
            var date=row["backup_created_at"] as DateTime?;
            var age=(int)row["backup_max_hours"]!;
            var aligned=row["backup_matches_device"] is true;
            var state=!aligned?"unknown":date.HasValue&&date<=now&&date>=now.AddHours(-age)?"pass":"fail";
            checks.Add(new("backup_freshness",state,!aligned?"Backup asset does not match router":
                date.HasValue?$"Newest stored version: {date.Value:O}; maximum age {age}h":"No stored backup version"));
        }
        if(row["require_baseline"] is true)
        {
            if(row["baseline"] is not string baseline)
                checks.Add(new("baseline","unknown","No approved configuration baseline"));
            else
            {
                var current=Normalize(snapshot.RootElement);
                var expectedSections=JsonSerializer.Deserialize<SortedDictionary<string,string[]>>(baseline,Json)!;
                foreach(var rule in Rules)
                {
                    if(!current.TryGetValue(rule.Name,out var observed)||!expectedSections.TryGetValue(rule.Name,out var saved))
                        checks.Add(new(rule.Name,"unknown","Section unavailable or incomplete"));
                    else
                    {
                        var same=observed.SequenceEqual(saved,StringComparer.Ordinal);
                        checks.Add(new(rule.Name,same?"pass":"fail",same?$"{observed.Length} records match":
                            $"Configuration differs: baseline {saved.Length}, observed {observed.Length} records"));
                    }
                }
            }
        }
        return checks;
    }
    private static bool BaselineEligible(Dictionary<string,object?> row)=>
        row["enabled"] is true&&row["address"] as string==row["current_address"] as string&&
        row["last_status"] as string=="ok"&&row["last_poll_at"] is DateTime observed&&
        observed<=DateTime.UtcNow&&observed>=DateTime.UtcNow.AddHours(-(int)row["snapshot_max_hours"]!)&&
        row["snapshot"] is string;
    private const string Evidence="""
        SELECT c.*,d.name,d.enabled,host(d.address) AS address,
          COALESCE(n.nasname,host(e.management_ip)) AS current_address,
          d.last_poll_at,d.last_status,d.snapshot::text AS snapshot,
          h.id AS highsite_id,h.name AS highsite,
          baseline.id AS baseline_id,baseline.sections::text AS baseline,baseline.created_at AS baseline_created_at,
          (SELECT max(v.created_at) FROM backup_versions v WHERE v.profile_id=c.backup_profile_id) AS backup_created_at,
          COALESCE((b.nas_id=d.nas_id OR b.equipment_id=d.equipment_id),false) AS backup_matches_device
        FROM routeros_compliance_policies c JOIN routeros_devices d ON d.id=c.device_id
        LEFT JOIN nas n ON n.id=d.nas_id LEFT JOIN equipment e ON e.id=d.equipment_id
        LEFT JOIN highsites h ON h.id=e.highsite_id
        LEFT JOIN backup_profiles b ON b.id=c.backup_profile_id
        LEFT JOIN LATERAL(
          SELECT id,sections,created_at FROM routeros_compliance_baselines
          WHERE device_id=d.id ORDER BY created_at DESC,id DESC LIMIT 1
        ) baseline ON true
        """;
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/compliance/devices",async(HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            // List metadata only: avoid returning many large JSON snapshots.
            return Results.Ok(await Store.Rows(db,"""
                SELECT d.id,d.name,host(d.address) AS address,d.enabled,d.last_poll_at,d.last_status,
                  h.name AS highsite,c.device_id IS NOT NULL AS configured,c.expected_version,
                  c.backup_profile_id,c.backup_max_hours,c.snapshot_max_hours,c.require_baseline,
                  c.poll_interval_minutes,c.next_poll_at
                FROM routeros_devices d LEFT JOIN routeros_compliance_policies c ON c.device_id=d.id
                LEFT JOIN equipment e ON e.id=d.equipment_id LEFT JOIN highsites h ON h.id=e.highsite_id
                ORDER BY d.name,d.id OFFSET @offset LIMIT 100
                """,P("offset",offset??0)));
        });
        api.MapGet("/compliance/devices/{id:guid}",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            var rows=await Store.Rows(db,Evidence+" WHERE c.device_id=@id",P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var row=rows.Single();
            var checks=Evaluate(row,DateTime.UtcNow);
            var status=checks.Any(x=>x.State=="fail")?"noncompliant":checks.Any(x=>x.State=="unknown")?"unknown":"compliant";
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{status,checks,baselineId=row["baseline_id"],baselineCreatedAt=row["baseline_created_at"],
                backupCreatedAt=row["backup_created_at"],lastPollAt=row["last_poll_at"]});
        });
        api.MapPut("/compliance/devices/{id:guid}/policy",async(Guid id,HttpContext ctx,NpgsqlDataSource db,CompliancePolicyInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var version=(input.ExpectedVersion??"").Trim();
            if(!input.Confirm||input.BackupMaxHours is <1 or >168||input.SnapshotMaxHours is <1 or >168||
                (input.PollIntervalMinutes!=0&&input.PollIntervalMinutes is <15 or >1440)||
                (version.Length>0&&(version.Length>32||!Regex.IsMatch(version,@"^[0-9]+\.[0-9]+(?:\.[0-9]+)?(?:[a-z]+[0-9]+)?$"))))
                return Invalid("Confirm valid exact RouterOS version, age limits and optional polling interval");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var device=await NetworkManagement.Query(conn,tx,"SELECT id FROM routeros_devices WHERE id=@id FOR UPDATE",P("id",id));
            if(device.Count!=1)return Results.NotFound();
            if(input.BackupProfileId is Guid profile&&
                (await NetworkManagement.Query(conn,tx,"""
                    SELECT b.id FROM backup_profiles b JOIN routeros_devices d ON (b.nas_id=d.nas_id OR b.equipment_id=d.equipment_id)
                    WHERE d.id=@id AND b.id=@profile AND b.vendor='routeros'
                    """,P("id",id),P("profile",profile))).Count!=1)
                return Conflict("Backup profile must belong to the same RouterOS inventory asset");
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO routeros_compliance_policies(device_id,expected_version,backup_profile_id,backup_max_hours,
                  snapshot_max_hours,require_baseline,poll_interval_minutes)
                VALUES(@id,@version,@backup,@age,@snapshot,@baseline,@interval)
                ON CONFLICT(device_id) DO UPDATE SET expected_version=EXCLUDED.expected_version,
                  backup_profile_id=EXCLUDED.backup_profile_id,backup_max_hours=EXCLUDED.backup_max_hours,
                  snapshot_max_hours=EXCLUDED.snapshot_max_hours,require_baseline=EXCLUDED.require_baseline,
                  poll_interval_minutes=EXCLUDED.poll_interval_minutes,next_poll_at=now(),updated_at=now()
                """,P("id",id),P("version",version.Length==0?null:version),P("backup",input.BackupProfileId),
                P("age",input.BackupMaxHours),P("snapshot",input.SnapshotMaxHours),P("baseline",input.RequireBaseline),P("interval",input.PollIntervalMinutes));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"compliance-policy","routeros_device",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapGet("/compliance/devices/{id:guid}/baseline-preview",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Denied();
            var rows=await Store.Rows(db,Evidence+" WHERE c.device_id=@id",P("id",id));
            if(rows.Count!=1)return Results.NotFound();
            var row=rows.Single();
            if(!BaselineEligible(row))return Conflict("A recent, complete and enabled inspection is required");
            using var snapshot=JsonDocument.Parse((string)row["snapshot"]!);
            var normalized=Normalize(snapshot.RootElement);
            if(normalized.Count!=Rules.Length)return Conflict("Configuration fields are missing");
            var body=JsonSerializer.Serialize(normalized,Json);
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{snapshotSha256=Hash((string)row["snapshot"]!),baselineSha256=Hash(body),
                sourcePollAt=row["last_poll_at"],sections=normalized});
        });
        api.MapPost("/compliance/devices/{id:guid}/baseline",async(Guid id,HttpContext ctx,NpgsqlDataSource db,ComplianceBaselineInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var reason=(input.Reason??"").Trim();
            if(!input.Confirm||reason.Length is <5 or >240)return Invalid("Confirm review of current configuration with a reason");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            // Lock device first (same ordering as poll and policy writes).
            if((await NetworkManagement.Query(conn,tx,"SELECT id FROM routeros_devices WHERE id=@id FOR UPDATE",P("id",id))).Count!=1)
                return Results.NotFound();
            var rows=await NetworkManagement.Query(conn,tx,Evidence+" WHERE c.device_id=@id",P("id",id));
            if(rows.Count!=1)return Conflict("Configure a compliance policy first");
            var row=rows.Single();
            if(!BaselineEligible(row))return Conflict("An enabled device with a recent successful complete inspection is required");
            var observed=(DateTime)row["last_poll_at"]!;var raw=(string)row["snapshot"]!;
            if(input.SnapshotSha256!=Hash(raw))return Conflict("Inspection changed or preview was not reviewed; reload baseline preview");
            using var snapshot=JsonDocument.Parse(raw);
            var normalized=Normalize(snapshot.RootElement);
            if(normalized.Count!=Rules.Length)return Conflict("Configuration fields are missing; no baseline captured");
            var count=await NetworkManagement.Query(conn,tx,"SELECT count(*) AS n FROM routeros_compliance_baselines WHERE device_id=@id",P("id",id));
            if((long)count[0]["n"]!>=100)return Conflict("Baseline history has reached 100 immutable revisions");
            var body=JsonSerializer.Serialize(normalized,Json);var baseline=Guid.NewGuid();
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO routeros_compliance_baselines(id,device_id,source_poll_at,sections,sha256,reason,created_by)
                VALUES(@baseline,@device,@observed,@body::jsonb,@hash,@reason,@user)
                """,P("baseline",baseline),P("device",id),P("observed",observed),P("body",body),P("hash",Hash(body)),
                P("reason",reason),P("user",Guid.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value)));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"compliance-baseline","routeros_device",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/compliance/devices/"+id+"/baseline",new{id=baseline,sha256=Hash(body)});
        });
        api.MapGet("/compliance/devices/{id:guid}/baselines",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT b.id,b.source_poll_at,b.sha256,b.reason,b.created_at,u.username AS author
                FROM routeros_compliance_baselines b JOIN app_users u ON u.id=b.created_by
                WHERE b.device_id=@id ORDER BY b.created_at DESC,b.id DESC LIMIT 100
                """,P("id",id)));
        });
    }
}

// One sequential worker per process. PollCore enforces shared concurrency,
// row locking, and due-time revalidation for multi-instance deployments.
public sealed class RouterOsComplianceWorker(NpgsqlDataSource db,IDataProtectionProvider dp,
    IConfiguration config,ILogger<RouterOsComplianceWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        while(!token.IsCancellationRequested)
        {
            try
            {
                var rows=await Store.Rows(db,"""
                    SELECT c.device_id FROM routeros_compliance_policies c JOIN routeros_devices d ON d.id=c.device_id
                    WHERE c.poll_interval_minutes>0 AND c.next_poll_at<=now() AND d.enabled
                    ORDER BY c.next_poll_at,c.device_id LIMIT 10
                    """);
                foreach(var row in rows)
                {
                    if(token.IsCancellationRequested)break;
                    await RouterOsInventory.PollCore((Guid)row["device_id"]!,db,dp,config,"compliance-worker",token,true);
                }
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception error){logger.LogWarning(error,"Scheduled RouterOS inspection failed");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
}
