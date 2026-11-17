// root/selfhost/src/NetForge.Api/RfTelemetry.cs
// Firmware-specific numeric OIDs supplied by operators; no invented vendor MIBs.
// Read-only SNMPv3 authPriv, historical measurements and debounced alarms.
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace NetForge.Api;

public sealed record RfMetricInput(Guid DeviceId,string MetricKey,string Oid,
    decimal Scale,decimal Offset,bool Enabled,string Direction,
    decimal? WarningThreshold,decimal? CriticalThreshold,decimal RecoveryMargin,
    bool Confirm);
public sealed record RfMetricUpdate(bool Enabled,string Direction,
    decimal? WarningThreshold,decimal? CriticalThreshold,decimal RecoveryMargin,
    bool Confirm);

public static class RfTelemetry
{
    private static NpgsqlParameter P(string key,object? value)=>Store.P(key,value);
    private static readonly Regex OidPattern=new(@"^\.[0-9]+(\.[0-9]+){4,30}$",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly HashSet<string> MetricKeys=new(StringComparer.Ordinal){
        "rssi_dbm","rssi_chain0_dbm","rssi_chain1_dbm","snr_db","noise_dbm",
        "ccq_pct","tx_ccq_pct","rx_capacity_mbps","tx_capacity_mbps",
        "cpu_pct","memory_pct","voltage_v","power_w","temperature_c","frequency_mhz"
    };
    private static bool Staff(HttpContext ctx)=>ctx.User.IsInRole("admin")||
        ctx.User.IsInRole("dispatcher");
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static IResult Denied()=>Results.Problem("Administrator permissions required",statusCode:403);
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";

    private static bool ValidOid(string? oid)
    {
        if(oid is null || oid.Length>128 || !OidPattern.IsMatch(oid))return false;
        var arcs=oid[1..].Split('.');
        // Vendor-specific roots must not resolve symbolic MIB strings or
        // malformed / overflow subidentifiers. Numeric-only SNMP access.
        return arcs.All(part=>uint.TryParse(part,NumberStyles.None,
            CultureInfo.InvariantCulture,out _)) &&
            arcs[0] is "0" or "1" or "2";
    }

    private static bool ValidThresholds(string direction,decimal? warning,
        decimal? critical,decimal margin)
    {
        if(direction is not ("below" or "above") || margin<0 || margin>100000)
            return false;
        if(warning.HasValue!=critical.HasValue)return false;
        if(!warning.HasValue)return true;
        if(Math.Abs(warning.Value)>1_000_000_000m ||
           Math.Abs(critical!.Value)>1_000_000_000m)return false;
        return direction=="below" ? critical<=warning : critical>=warning;
    }

    private static bool ValidInput(RfMetricInput input)=>
        MetricKeys.Contains(input.MetricKey??"") &&
        ValidOid(input.Oid) && input.Scale!=0 &&
        Math.Abs(input.Scale)<=100000m &&
        Math.Abs(input.Offset)<=100000m &&
        ValidThresholds(input.Direction,input.WarningThreshold,
            input.CriticalThreshold,input.RecoveryMargin) &&
        (!input.Enabled || input.Confirm);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/telemetry/metrics",async(HttpContext ctx,
            NpgsqlDataSource db,Guid? deviceId)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.id,p.device_id,d.name AS device,p.metric_key,p.oid,
                    p.scale,p.value_offset,p.enabled,p.direction,
                    p.warning_threshold,p.critical_threshold,p.recovery_margin,
                    p.severity,p.pending_severity,p.pending_count,p.last_value,
                    p.last_checked_at
                FROM snmp_metric_profiles p JOIN snmp_devices d ON d.id=p.device_id
                WHERE @device IS NULL OR p.device_id=@device
                ORDER BY d.name,p.metric_key LIMIT 2000
                """,P("device",deviceId)));
        });
        api.MapPost("/telemetry/metrics",async(HttpContext ctx,
            NpgsqlDataSource db,RfMetricInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!ValidInput(input))return Results.BadRequest(new{
                error="Select a supported metric, numeric OID, valid scale and ordered thresholds; confirm enabling"});
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var devices=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                "SELECT id FROM snmp_devices WHERE id=@id FOR UPDATE",
                P("id",input.DeviceId));
            if(devices.Count==0)return Results.NotFound(new{error="SNMPv3 device not found"});
            var count=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                "SELECT count(*) AS total FROM snmp_metric_profiles WHERE device_id=@id",
                P("id",input.DeviceId));
            if((long)count.Single()["total"]!>=12)return Results.Conflict(new{
                error="Maximum 12 custom OID metrics per SNMP device"});
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO snmp_metric_profiles(
                    device_id,metric_key,oid,scale,value_offset,enabled,
                    direction,warning_threshold,critical_threshold,recovery_margin)
                VALUES(@device,@key,@oid,@scale,@offset,@enabled,
                    @direction,@warning,@critical,@margin)
                ON CONFLICT(device_id,metric_key) DO NOTHING
                RETURNING id
                """,P("device",input.DeviceId),P("key",input.MetricKey),
                P("oid",input.Oid),P("scale",input.Scale),P("offset",input.Offset),
                P("enabled",input.Enabled),P("direction",input.Direction),
                P("warning",input.WarningThreshold),P("critical",input.CriticalThreshold),
                P("margin",input.RecoveryMargin));
            if(rows.Count==0)return Results.Conflict(new{
                error="That metric key is already configured for this SNMP device"});
            var id=(Guid)rows.Single()["id"]!;
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "telemetry-metric-create","snmp_metric",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/telemetry/metrics/"+id,new{id,
                note="Numeric OID stored; verify firmware semantics and units with the vendor before interpreting measurements."});
        });
        api.MapPut("/telemetry/metrics/{id:guid}",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,RfMetricUpdate input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!ValidThresholds(input.Direction,input.WarningThreshold,
                input.CriticalThreshold,input.RecoveryMargin) ||
                (input.Enabled&&!input.Confirm))
                return Results.BadRequest(new{error="Valid ordered thresholds and confirmation are required"});
            var updated=await Store.Execute(db,"""
                UPDATE snmp_metric_profiles
                SET enabled=@enabled,direction=@direction,warning_threshold=@warning,
                    critical_threshold=@critical,recovery_margin=@margin,
                    severity='normal',pending_severity='normal',pending_count=0
                WHERE id=@id
                """,P("id",id),P("enabled",input.Enabled),
                P("direction",input.Direction),
                P("warning",input.WarningThreshold),
                P("critical",input.CriticalThreshold),
                P("margin",input.RecoveryMargin));
            if(updated==0)return Results.NotFound();
            await Store.Audit(db,Actor(ctx),"telemetry-metric-policy","snmp_metric",id.ToString());
            return Results.Ok(new{ok=true,
                note="Threshold state reset to normal. Existing event history remains intact."});
        });
        api.MapGet("/telemetry/metrics/{id:guid}/history",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,int? hours,string? resolution)=>
        {
            if(!Staff(ctx))return Denied();
            if(hours is <1 or >43200 ||
               resolution is not (null or "raw" or "hourly"))
                return Results.BadRequest(new{error="Choose raw/hourly and 1–43200 hours"});
            var found=await Store.Rows(db,"SELECT id FROM snmp_metric_profiles WHERE id=@id",
                P("id",id));
            if(found.Count==0)return Results.NotFound();
            if(resolution=="hourly")
                return Results.Ok(await Store.Rows(db,"""
                    SELECT hour_start,samples,minimum,maximum,average
                    FROM (
                        SELECT hour_start,samples,minimum,maximum,average
                        FROM snmp_metric_hourly
                        WHERE profile_id=@id
                          AND hour_start>=now()-make_interval(hours=>@hours)
                        ORDER BY hour_start DESC LIMIT 1000
                    ) h ORDER BY hour_start
                    """,P("id",id),P("hours",hours??168)));
            return Results.Ok(await Store.Rows(db,"""
                SELECT observed_at,value,raw_value FROM (
                    SELECT id,observed_at,value,raw_value
                    FROM snmp_metric_samples
                    WHERE profile_id=@id
                      AND observed_at>=now()-make_interval(hours=>@hours)
                    ORDER BY observed_at DESC,id DESC LIMIT 1000
                ) recent ORDER BY observed_at
                """,P("id",id),P("hours",hours??168)));
        });
        api.MapGet("/telemetry/events",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT e.id,e.profile_id,d.name AS device,p.metric_key,
                    e.previous_severity,e.severity,e.value,e.suppressed,e.observed_at
                FROM snmp_metric_events e JOIN snmp_metric_profiles p ON p.id=e.profile_id
                JOIN snmp_devices d ON d.id=p.device_id
                ORDER BY e.observed_at DESC LIMIT 500
                """));
        });
        api.MapGet("/telemetry/sites",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT h.id,h.name,
                    count(p.id) FILTER(WHERE p.enabled AND d.enabled) AS metrics,
                    count(p.id) FILTER(WHERE p.enabled AND d.enabled
                        AND p.severity='warning') AS warnings,
                    count(p.id) FILTER(WHERE p.enabled AND d.enabled
                        AND p.severity='critical') AS critical
                FROM highsites h
                LEFT JOIN snmp_devices d ON d.highsite_id=h.id
                LEFT JOIN snmp_metric_profiles p ON p.device_id=d.id
                GROUP BY h.id,h.name
                ORDER BY critical DESC,warnings DESC,h.name
                """));
        });
    }

    internal static decimal? ParseValue(string raw)
    {
        // SNMP -Oqv should be a plain numeric scalar for configured vendor
        // OIDs. Strings, multi-values, "No Such Object" and NaN are rejected.
        var trimmed=raw.Trim();
        if(trimmed.Length==0||trimmed.Length>32)return null;
        if(!decimal.TryParse(trimmed,
            NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,out var n))return null;
        return Math.Abs(n)<=1_000_000_000m?n:null;
    }

    private static string ObservedLevel(decimal value,string direction,
        decimal warning,decimal critical)
    {
        var isCritical=direction=="below"?value<=critical:value>=critical;
        var isWarning=direction=="below"?value<=warning:value>=warning;
        return isCritical?"critical":isWarning?"warning":"normal";
    }

    private static bool HasRecovered(decimal value,string direction,
        decimal threshold,decimal margin)=>
        direction=="below"?value>threshold+margin:value<threshold-margin;

    internal static (string Severity,string Pending,int Count,bool Changed) Evaluate(
        string current,string pending,int pendingCount,decimal value,
        string direction,decimal? warning,decimal? critical,decimal margin)
    {
        if(!warning.HasValue||!critical.HasValue)
            return ("normal","normal",0,current!="normal");
        var desired=ObservedLevel(value,direction,warning.Value,critical.Value);
        if(current=="critical" && desired!="critical")
        {
            if(!HasRecovered(value,direction,critical.Value,margin))
                desired="critical";
            else if(desired=="normal" &&
                !HasRecovered(value,direction,warning.Value,margin))
                desired="warning";
        }
        if(current=="warning" && desired=="normal" &&
            !HasRecovered(value,direction,warning.Value,margin))
            desired="warning";
        if(desired==current)return(current,"normal",0,false);
        var count=pending==desired?Math.Min(2,pendingCount+1):1;
        if(count<2)return(current,desired,count,false);
        return(desired,"normal",0,true);
    }

    internal static async Task<int> PollProfiles(NpgsqlConnection conn,
        NpgsqlTransaction tx,IConfiguration configuration,Guid deviceId,
        IPAddress address,int udpPort,Guid? highsite,string deviceName,
        string username,string authProtocol,IDataProtectionProvider protector,
        string authCiphertext,string privacyCiphertext,CancellationToken token)
    {
        var profiles=await NetworkManagement.Query(conn,tx,"""
            SELECT id,metric_key,oid,scale,value_offset,direction,
                   warning_threshold,critical_threshold,recovery_margin,
                   severity,pending_severity,pending_count
            FROM snmp_metric_profiles
            WHERE device_id=@device AND enabled=true
            ORDER BY metric_key LIMIT 12
            """,P("device",deviceId));
        if(profiles.Count==0)return 0;
        string[] results;
        try
        {
            var cipher=protector.CreateProtector("NetForge.SnmpV3.Credentials.v1");
            var auth=cipher.Unprotect(authCiphertext);
            var privacy=cipher.Unprotect(privacyCiphertext);
            var path=configuration["NETFORGE_SNMPGET_PATH"];
            if(string.IsNullOrWhiteSpace(path))path="/usr/bin/snmpget";
            results=await SnmpTelemetry.QueryNetSnmpValues(path,address,udpPort,
                profiles.Select(p=>(string)p["oid"]!).ToArray(),
                username,authProtocol,auth,privacy,token);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception)
        {
            // A bad optional vendor OID must not change the device IF-MIB state.
            // No events or values are created when custom metric reads fail.
            return 0;
        }
        var count=0;
        for(var i=0;i<profiles.Count;i++)
        {
            var raw=ParseValue(results[i]);
            if(raw is null)continue;
            var profile=profiles[i];
            var scale=(decimal)profile["scale"]!;
            var offset=(decimal)profile["value_offset"]!;
            var value=raw.Value*scale+offset;
            if(Math.Abs(value)>1_000_000_000m)continue;
            var profileId=(Guid)profile["id"]!;
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO snmp_metric_samples(profile_id,raw_value,value)
                VALUES(@id,@raw,@value)
                """,P("id",profileId),P("raw",raw),P("value",value));
            var current=(string)profile["severity"]!;
            var (next,pending,pendingCount,changed)=Evaluate(current,
                (string)profile["pending_severity"]!,
                Convert.ToInt32(profile["pending_count"],CultureInfo.InvariantCulture),
                value,(string)profile["direction"]!,
                profile["warning_threshold"] as decimal?,
                profile["critical_threshold"] as decimal?,
                (decimal)profile["recovery_margin"]!);
            await NetworkManagement.Execute(conn,tx,"""
                UPDATE snmp_metric_profiles SET last_value=@value,last_checked_at=now(),
                    severity=@severity,pending_severity=@pending,
                    pending_count=@pending_count WHERE id=@id
                """,P("value",value),P("severity",next),P("pending",pending),
                P("pending_count",pendingCount),P("id",profileId));
            if(changed)
            {
                var suppressed=await SiteImpact.IsSuppressed(conn,tx,highsite);
                var events=await NetworkManagement.Query(conn,tx,"""
                    INSERT INTO snmp_metric_events(profile_id,previous_severity,
                        severity,value,suppressed)
                    VALUES(@id,@old,@next,@value,@suppressed) RETURNING id
                    """,P("id",profileId),P("old",current),P("next",next),
                    P("value",value),P("suppressed",suppressed));
                if(!suppressed)
                {
                    var eventId=(Guid)events.Single()["id"]!;
                    var label=(string)profile["metric_key"]!;
                    await Notifications.EnqueueMonitorEvent(conn,tx,eventId,
                        "NetForge telemetry "+deviceName+" / "+label+" is "+
                        next.ToUpperInvariant()+" (previous "+current+"): "+
                        value.ToString("0.####",CultureInfo.InvariantCulture)+
                        ". This is a vendor-specific OID; verify its units and firmware mapping.",
                        configuration);
                }
            }
            count++;
        }
        return count;
    }
}
