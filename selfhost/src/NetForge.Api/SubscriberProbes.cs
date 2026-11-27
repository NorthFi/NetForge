// root/selfhost/src/NetForge.Api/SubscriberProbes.cs
// Push-only subscriber-side DNS/HTTPS evidence. The API never contacts agent
// targets or sends remote commands. No interruption, SLA or credit is inferred.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace NetForge.Api;

public sealed record ProbeEnrollment(Guid ServiceId,string Name,string DnsHost,
    string HttpsUrl,int ExpectedStatus,int IntervalSeconds,bool Confirm);
public sealed record ProbeConfirmation(bool Confirm);
public sealed record ProbeReport(Guid MeasurementId,DateTimeOffset MeasuredAt,
    string DnsHost,string HttpsUrl,string DnsResult,int DnsMs,
    string HttpsResult,int HttpsMs,int? HttpsStatus);

public static class SubscriberProbes
{
    private static NpgsqlParameter P(string key,object? value)=>Store.P(key,value);
    private static bool Staff(HttpContext ctx)=>AssuranceAgreements.Staff(ctx);
    private static bool Admin(HttpContext ctx)=>AssuranceAgreements.Admin(ctx);
    private static IResult Denied()=>AssuranceAgreements.Denied();
    private static IResult Invalid(string msg)=>AssuranceAgreements.Invalid(msg);
    private static IResult Conflict(string msg)=>AssuranceAgreements.Conflict(msg);
    private static string Actor(HttpContext ctx)=>AssuranceAgreements.Actor(ctx);
    private static string NewToken()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
    private static bool CheckToken(string token,string stored)=>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)),Encoding.ASCII.GetBytes(stored.Trim()));
    private static bool DnsName(string host)=>host.Length is >=3 and <=253 &&
        host.Contains('.') && Uri.CheckHostName(host)==UriHostNameType.Dns &&
        host.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '-') &&
        host.Split('.').All(label=>label.Length is >=1 and <=63 && label[0]!='-' && label[^1]!='-');
    private static bool HttpsTarget(string url)=>url.Length<=512 &&
        url.All(c=>c is >= '!' and <= '~') && !url.Contains('\\') &&
        Uri.TryCreate(url,UriKind.Absolute,out var parsed) &&
        parsed.Scheme==Uri.UriSchemeHttps && parsed.Port==443 &&
        DnsName(parsed.Host) && parsed.UserInfo=="" && parsed.Query=="" && parsed.Fragment=="";
    private static bool ValidResult(string? result)=>result is "ok" or "failed" or "timeout";
    private static bool ValidReport(ProbeReport report,int expected)=>
        report.MeasurementId!=Guid.Empty && ValidResult(report.DnsResult) &&
        ValidResult(report.HttpsResult) && report.DnsMs is >=0 and <=30000 &&
        report.HttpsMs is >=0 and <=30000 &&
        (report.HttpsStatus is null or (>=100 and <=599)) &&
        (report.HttpsResult=="ok" ? report.HttpsStatus==expected :
         report.HttpsResult=="timeout" ? report.HttpsStatus is null :
         report.HttpsStatus!=expected);

    public static void MapStaff(RouteGroupBuilder api)
    {
        api.MapGet("/assurance/subscriber-probes",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            // Each vantage is shown separately. No stale report or CPE state is
            // promoted into measured downtime or a root-cause assertion.
            return Results.Ok(await Store.Rows(db,"""
                SELECT a.id,a.service_id,a.name,s.username,c.account,
                    a.dns_host,a.https_url,a.expected_status,a.interval_seconds,
                    a.enabled,a.generation,a.last_received_at,a.last_measured_at,
                    r.dns_result,r.dns_ms,r.https_result,r.https_ms,r.https_status,
                    CASE WHEN NOT a.enabled THEN 'disabled'
                         WHEN r.id IS NULL OR r.measured_at<now()-make_interval(secs=>a.interval_seconds*3)
                           OR r.received_at<now()-make_interval(secs=>a.interval_seconds*3)
                           THEN 'unknown'
                         WHEN r.dns_result='ok' AND r.https_result='ok' THEN 'checks_passed'
                         ELSE 'check_failed' END AS subscriber_evidence,
                    CASE WHEN p.enabled AND p.last_probed_at>=now()-make_interval(secs=>p.interval_seconds*3)
                         THEN p.state ELSE 'unknown' END AS cpe_evidence,
                    CASE WHEN EXISTS(SELECT 1 FROM radacct x WHERE x.username=s.username
                        AND x.acctstoptime IS NULL AND
                        COALESCE(x.acctupdatetime,x.acctstarttime)>=now()-make_interval(mins=>cfg.accounting_freshness_minutes)
                        AND COALESCE(x.acctupdatetime,x.acctstarttime)<=now()+interval '1 minute')
                        THEN 'recent_accounting' ELSE 'unknown' END AS radius_evidence,
                    a.created_at
                FROM subscriber_probe_agents a
                JOIN services s ON s.id=a.service_id JOIN customers c ON c.id=s.customer_id
                CROSS JOIN assurance_settings cfg
                LEFT JOIN assurance_cpe_probes p ON p.service_id=s.id
                LEFT JOIN LATERAL (SELECT * FROM subscriber_probe_reports x
                    WHERE x.agent_id=a.id AND x.generation=a.generation
                    ORDER BY x.measured_at DESC,x.id DESC LIMIT 1) r ON true
                ORDER BY c.account,s.username,a.name LIMIT 3000
                """));
        });
        api.MapPost("/assurance/subscriber-probes",async(HttpContext ctx,NpgsqlDataSource db,ProbeEnrollment input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=(input.Name??"").Trim();
            var host=(input.DnsHost??"").Trim().ToLowerInvariant();
            var url=(input.HttpsUrl??"").Trim();
            if(!input.Confirm || input.ServiceId==Guid.Empty || name.Length is <1 or >100 ||
               !DnsName(host)||!HttpsTarget(url)||input.ExpectedStatus is <200 or >299 ||
               input.IntervalSeconds is <60 or >3600)
                return Invalid("Confirm an authorized subscriber agent with a DNS name, HTTPS port 443 URL without query/credentials, status 200–299 and interval 60–3600 seconds");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var service=await NetworkManagement.Query(conn,tx,
                "SELECT id FROM services WHERE id=@id FOR UPDATE",P("id",input.ServiceId));
            if(service.Count==0)return Results.NotFound();
            var count=await NetworkManagement.Query(conn,tx,
                "SELECT count(*) AS total FROM subscriber_probe_agents WHERE service_id=@id",P("id",input.ServiceId));
            if((long)count.Single()["total"]!>=4)return Conflict("Maximum four vantage points per service");
            var token=NewToken();
            var rows=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO subscriber_probe_agents(service_id,name,dns_host,https_url,
                    expected_status,interval_seconds,token_hash,created_by)
                VALUES(@service,@name,@host,@url,@status,@interval,@hash,@actor)
                ON CONFLICT(service_id,name) DO NOTHING RETURNING id
                """,P("service",input.ServiceId),P("name",name),P("host",host),P("url",url),
                P("status",input.ExpectedStatus),P("interval",input.IntervalSeconds),
                P("hash",Hash(token)),P("actor",Actor(ctx)));
            if(rows.Count==0)return Conflict("Vantage name already exists for this service");
            var id=(Guid)rows.Single()["id"]!;
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"subscriber-probe-enroll","subscriber_probe",id.ToString());
            await tx.CommitAsync();
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Created("/api/assurance/subscriber-probes/"+id,
                new{id,token,dnsHost=host,httpsUrl=url,expectedStatus=input.ExpectedStatus,intervalSeconds=input.IntervalSeconds});
        });
        api.MapPost("/assurance/subscriber-probes/{id:guid}/revoke",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,ProbeConfirmation input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm revoking this agent");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var found=await NetworkManagement.Query(conn,tx,"""
                UPDATE subscriber_probe_agents SET enabled=false WHERE id=@id RETURNING id
                """,P("id",id));
            if(found.Count==0)return Results.NotFound();
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"subscriber-probe-revoke","subscriber_probe",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/assurance/subscriber-probes/{id:guid}/rotate",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,ProbeConfirmation input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm rotating credentials and re-enabling this agent");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var token=NewToken();
            var rows=await NetworkManagement.Query(conn,tx,"""
                UPDATE subscriber_probe_agents SET token_hash=@hash,enabled=true,
                    generation=generation+1,last_received_at=NULL,last_measured_at=NULL
                WHERE id=@id RETURNING dns_host,https_url,expected_status,interval_seconds
                """,P("hash",Hash(token)),P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var row=rows.Single();
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"subscriber-probe-rotate","subscriber_probe",id.ToString());
            await tx.CommitAsync();
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{id,token,dnsHost=row["dns_host"],httpsUrl=row["https_url"],
                expectedStatus=row["expected_status"],intervalSeconds=row["interval_seconds"]});
        });
        api.MapGet("/assurance/subscriber-probes/{id:guid}/history",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,int? hours)=>
        {
            if(!Staff(ctx))return Denied();
            if(hours is <1 or >720)return Invalid("Hours must be 1–720");
            var found=await Store.Rows(db,"SELECT id FROM subscriber_probe_agents WHERE id=@id",P("id",id));
            if(found.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT generation,measured_at,received_at,dns_result,dns_ms,
                    https_result,https_ms,https_status FROM (
                    SELECT * FROM subscriber_probe_reports WHERE agent_id=@id
                    AND measured_at>=now()-make_interval(hours=>@hours)
                    ORDER BY measured_at DESC,id DESC LIMIT 1000
                ) recent ORDER BY measured_at,id
                """,P("id",id),P("hours",hours??24)));
        });
    }

    public static void MapIngest(WebApplication app)
    {
        app.MapPost("/api/probe-report/{id:guid}",Ingest)
            .AllowAnonymous().RequireRateLimiting("probe-ingest");
    }

    private static async Task<IResult> Ingest(Guid id,HttpContext ctx,NpgsqlDataSource db)
    {
        // Never accept staff/portal cookies as agent authentication. The
        // bearer token is a random 256-bit capability scoped to one agent.
        ctx.Response.Headers.CacheControl="no-store";
        var auth=ctx.Request.Headers.Authorization.ToString();
        if(!auth.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)||auth.Length!=71)
            return Results.Unauthorized();
        var token=auth[7..];
        if(!token.All(c=>char.IsAsciiHexDigit(c)))return Results.Unauthorized();
        if(ctx.Request.ContentLength is not (>0 and <=4096) ||
           !ctx.Request.HasJsonContentType())
            return Invalid("Send a bounded JSON report with Content-Length (1–4096 bytes)");
        ProbeReport? report;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            report=await ctx.Request.ReadFromJsonAsync<ProbeReport>(cancellationToken:timeout.Token);
        }
        catch(JsonException){return Invalid("Malformed report JSON");}
        catch(OperationCanceledException){return Results.StatusCode(408);}
        if(report is null)return Invalid("Report required");
        await using var conn=await db.OpenConnectionAsync(ctx.RequestAborted);
        await using var tx=await conn.BeginTransactionAsync(ctx.RequestAborted);
        var found=await NetworkManagement.Query(conn,tx,"""
            SELECT enabled,token_hash,generation,dns_host,https_url,expected_status,
                last_received_at,last_measured_at FROM subscriber_probe_agents
            WHERE id=@id FOR UPDATE
            """,P("id",id));
        if(found.Count==0)return Results.Unauthorized();
        var row=found.Single();
        if(row["enabled"] is not true || !CheckToken(token,(string)row["token_hash"]!))
            return Results.Unauthorized();
        var expected=(int)row["expected_status"]!;
        if(!ValidReport(report,expected)||report.DnsHost!=(string)row["dns_host"]!||
           report.HttpsUrl!=(string)row["https_url"]!)
            return Invalid("Invalid result or configured target mismatch");
        var duplicate=await NetworkManagement.Query(conn,tx,"""
            SELECT id FROM subscriber_probe_reports WHERE agent_id=@id AND measurement_id=@measurement
            """,P("id",id),P("measurement",report.MeasurementId));
        if(duplicate.Count>0)return Results.Ok(new{ok=true,replayed=true});
        var now=DateTime.UtcNow;
        var measured=report.MeasuredAt.UtcDateTime;
        if(measured<now.AddMinutes(-10)||measured>now.AddMinutes(1))
            return Invalid("Measurement timestamp must be within 10 minutes past / 1 minute future");
        if(row["last_measured_at"] is DateTime previous && measured<=previous)
            return Conflict("Measurement is older than the latest accepted report");
        if(row["last_received_at"] is DateTime last && last>now.AddSeconds(-30))
            return Results.StatusCode(429);
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO subscriber_probe_reports(agent_id,generation,measurement_id,
                measured_at,dns_result,dns_ms,https_result,https_ms,https_status)
            VALUES(@id,@generation,@measurement,@measured,@dns,@dns_ms,@https,@https_ms,@status)
            """,P("id",id),P("generation",row["generation"]),P("measurement",report.MeasurementId),
            P("measured",measured),P("dns",report.DnsResult),P("dns_ms",report.DnsMs),
            P("https",report.HttpsResult),P("https_ms",report.HttpsMs),P("status",report.HttpsStatus));
        await NetworkManagement.Execute(conn,tx,"""
            UPDATE subscriber_probe_agents SET last_received_at=now(),last_measured_at=@measured
            WHERE id=@id
            """,P("id",id),P("measured",measured));
        await tx.CommitAsync(ctx.RequestAborted);
        return Results.Ok(new{ok=true,replayed=false});
    }
}
