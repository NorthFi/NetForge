// root/selfhost/src/NetForge.Api/TelemetryRetention.cs
// Non-destructive hourly rollups first, then bounded opt-in raw-data cleanup.
// Cleanup defaults OFF; retention changes require administrator confirmation.
using Npgsql;

namespace NetForge.Api;

public sealed record TelemetryRetentionInput(bool Enabled,int RawDays,int HourlyDays,bool Confirm);
public sealed record TelemetryMaintenanceInput(bool Confirm);
public sealed record TelemetryMaintenanceResult(bool Enabled,int RfHours,int InterfaceHours,
    int RfRawDeleted,int InterfaceRawDeleted,int RfHourlyDeleted,
    int InterfaceHourlyDeleted,string Note);

public static class TelemetryRetention
{
    private static NpgsqlParameter P(string key,object? value)=>Store.P(key,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher");
    private static IResult Denied()=>Results.Problem("Administrator permissions required",statusCode:403);
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/telemetry/retention",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Denied();
            var settings=await Store.Rows(db,"""
                SELECT enabled,raw_days,hourly_days,last_run_at,updated_at
                FROM telemetry_retention WHERE id=1
                """);
            return Results.Ok(new{settings=settings.Single(),note=
                "Retention disabled by default; enable only after testing database backup/restore."});
        });
        api.MapPut("/telemetry/retention",async(HttpContext ctx,NpgsqlDataSource db,
            TelemetryRetentionInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm||input.RawDays is <7 or >365 ||
               input.HourlyDays is <30 or >1825 ||
               input.HourlyDays<input.RawDays)
                return Results.BadRequest(new{error=
                    "Confirm a raw retention of 7–365 days and hourly retention 30–1825 days, with hourly >= raw."});
            await Store.Execute(db,"""
                UPDATE telemetry_retention
                SET enabled=@enabled,raw_days=@raw,hourly_days=@hourly,updated_at=now()
                WHERE id=1
                """,P("enabled",input.Enabled),P("raw",input.RawDays),
                P("hourly",input.HourlyDays));
            await Store.Audit(db,Actor(ctx),"telemetry-retention-policy","telemetry_retention","1");
            return Results.Ok(new{ok=true,
                note="Settings saved. When enabled, expired raw and aggregate samples are irreversibly pruned after hourly rollup."});
        });
        api.MapPost("/telemetry/retention/run",async(HttpContext ctx,
            NpgsqlDataSource db,TelemetryMaintenanceInput input,CancellationToken token)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)
                return Results.BadRequest(new{error="Explicit confirmation required before retention cleanup"});
            var result=await Run(db,token);
            await Store.Audit(db,Actor(ctx),"telemetry-retention-run","telemetry_retention","1");
            return Results.Ok(result);
        });
        api.MapGet("/telemetry/interface-hourly/{id:guid}",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,int? hours)=>
        {
            if(!Staff(ctx))return Denied();
            if(hours is <1 or >43200)
                return Results.BadRequest(new{error="Hours must be 1–43200"});
            var found=await Store.Rows(db,"SELECT id FROM snmp_devices WHERE id=@id",P("id",id));
            if(found.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT hour_start,samples,operational_samples,input_average_bps,
                    input_peak_bps,output_average_bps,output_peak_bps FROM (
                    SELECT * FROM snmp_interface_hourly
                    WHERE device_id=@id AND hour_start>=now()-make_interval(hours=>@hours)
                    ORDER BY hour_start DESC LIMIT 1000
                ) recent ORDER BY hour_start
                """,P("id",id),P("hours",hours??2160)));
        });
    }

    public static async Task<TelemetryMaintenanceResult> Run(
        NpgsqlDataSource db,CancellationToken cancellation)
    {
        await using var conn=await db.OpenConnectionAsync(cancellation);
        await using var tx=await conn.BeginTransactionAsync(cancellation);
        var locks=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
            "SELECT pg_try_advisory_xact_lock(749205) AS acquired");
        if(locks.Single()["acquired"] is not true)
            return new TelemetryMaintenanceResult(false,0,0,0,0,0,0,
                "Another maintenance run is active");
        var settings=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            SELECT enabled,raw_days,hourly_days FROM telemetry_retention
            WHERE id=1 FOR UPDATE
            """);
        var setting=settings.Single();
        if(setting["enabled"] is not true)
            return new TelemetryMaintenanceResult(false,0,0,0,0,0,0,
                "Telemetry retention is disabled; nothing was deleted or rolled up.");
        var rawDays=(int)setting["raw_days"]!;
        var hourlyDays=(int)setting["hourly_days"]!;

        // Summarize only COMPLETED UTC-hour windows not previously summarized.
        // Previous hours whose raw history was pruned are never recalculated
        // from partial data; incomplete/newly collected hours stay in raw.
        var rf=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            WITH grouped AS (
                SELECT s.profile_id,date_trunc('hour',s.observed_at) AS hour_start,
                    count(*)::integer AS samples,min(s.value) AS minimum,
                    max(s.value) AS maximum,round(avg(s.value),4) AS average
                FROM snmp_metric_samples s
                WHERE s.observed_at<date_trunc('hour',now())
                  AND NOT EXISTS (
                    SELECT 1 FROM snmp_metric_hourly h
                    WHERE h.profile_id=s.profile_id AND
                      h.hour_start=date_trunc('hour',s.observed_at)
                  )
                GROUP BY s.profile_id,date_trunc('hour',s.observed_at)
                ORDER BY hour_start LIMIT 10000
            ), added AS (
                INSERT INTO snmp_metric_hourly(profile_id,hour_start,samples,
                    minimum,maximum,average)
                SELECT profile_id,hour_start,samples,minimum,maximum,average
                FROM grouped ON CONFLICT DO NOTHING RETURNING 1
            ) SELECT count(*) AS total FROM added
            """);
        var standard=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            WITH grouped AS (
                SELECT s.device_id,date_trunc('hour',s.observed_at) AS hour_start,
                    count(*)::integer AS samples,
                    count(*) FILTER(WHERE s.oper_status=1)::integer AS operational_samples,
                    round(avg(s.input_bps),2) AS input_average_bps,
                    max(s.input_bps) AS input_peak_bps,
                    round(avg(s.output_bps),2) AS output_average_bps,
                    max(s.output_bps) AS output_peak_bps
                FROM snmp_samples s
                WHERE s.observed_at<date_trunc('hour',now())
                  AND NOT EXISTS (
                    SELECT 1 FROM snmp_interface_hourly h
                    WHERE h.device_id=s.device_id AND
                      h.hour_start=date_trunc('hour',s.observed_at)
                  )
                GROUP BY s.device_id,date_trunc('hour',s.observed_at)
                ORDER BY hour_start LIMIT 10000
            ), added AS (
                INSERT INTO snmp_interface_hourly(device_id,hour_start,samples,
                    operational_samples,input_average_bps,input_peak_bps,
                    output_average_bps,output_peak_bps)
                SELECT device_id,hour_start,samples,operational_samples,
                    input_average_bps,input_peak_bps,output_average_bps,
                    output_peak_bps
                FROM grouped ON CONFLICT DO NOTHING RETURNING 1
            ) SELECT count(*) AS total FROM added
            """);
        var rfCount=checked((int)(long)rf.Single()["total"]!);
        var interfaceCount=checked((int)(long)standard.Single()["total"]!);

        // Bounded deletes allow the operator to stop a cleanup run; each
        // chunk is protected by the same transaction as completed rollups.
        var rawMetric=0;var rawInterface=0;
        for(var n=0;n<20;n++)
        {
            cancellation.ThrowIfCancellationRequested();
            var metricRows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                WITH removed AS (
                    DELETE FROM snmp_metric_samples s WHERE id IN (
                        SELECT old.id FROM snmp_metric_samples old
                        WHERE old.observed_at<date_trunc('hour',now()-make_interval(days=>@days))
                          AND EXISTS (
                            SELECT 1 FROM snmp_metric_hourly h WHERE h.profile_id=old.profile_id
                              AND h.hour_start=date_trunc('hour',old.observed_at))
                        LIMIT 10000
                    ) RETURNING 1
                ) SELECT count(*) AS total FROM removed
                """,P("days",rawDays));
            var interfaceRows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                WITH removed AS (
                    DELETE FROM snmp_samples s WHERE id IN (
                        SELECT old.id FROM snmp_samples old
                        WHERE old.observed_at<date_trunc('hour',now()-make_interval(days=>@days))
                          AND EXISTS (
                            SELECT 1 FROM snmp_interface_hourly h WHERE h.device_id=old.device_id
                              AND h.hour_start=date_trunc('hour',old.observed_at))
                        LIMIT 10000
                    ) RETURNING 1
                ) SELECT count(*) AS total FROM removed
                """,P("days",rawDays));
            var a=checked((int)(long)metricRows.Single()["total"]!);
            var b=checked((int)(long)interfaceRows.Single()["total"]!);
            rawMetric+=a;rawInterface+=b;
            if(a==0 && b==0)break;
        }
        // Old rollups outlive raw samples, and are pruned only after the
        // independently configured long-term retention boundary.
        var rfOld=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            WITH removed AS(
                DELETE FROM snmp_metric_hourly WHERE (profile_id,hour_start) IN(
                    SELECT profile_id,hour_start FROM snmp_metric_hourly
                    WHERE hour_start<date_trunc('hour',now()-make_interval(days=>@days))
                    LIMIT 10000
                ) RETURNING 1
            ) SELECT count(*) AS total FROM removed
            """,P("days",hourlyDays));
        var interfaceOld=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            WITH removed AS(
                DELETE FROM snmp_interface_hourly WHERE (device_id,hour_start) IN(
                    SELECT device_id,hour_start FROM snmp_interface_hourly
                    WHERE hour_start<date_trunc('hour',now()-make_interval(days=>@days))
                    LIMIT 10000
                ) RETURNING 1
            ) SELECT count(*) AS total FROM removed
            """,P("days",hourlyDays));
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
            "UPDATE telemetry_retention SET last_run_at=now() WHERE id=1");
        await tx.CommitAsync(cancellation);
        return new TelemetryMaintenanceResult(true,rfCount,interfaceCount,
            rawMetric,rawInterface,
            checked((int)(long)rfOld.Single()["total"]!),
            checked((int)(long)interfaceOld.Single()["total"]!),
            "One transaction completed. Deletes are bounded to 200,000 records per raw table and 10,000 per hourly table; rerun if backlog remains.");
    }
}

public sealed class TelemetryRetentionWorker(NpgsqlDataSource db,
    ILogger<TelemetryRetentionWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromHours(6));
        while(!token.IsCancellationRequested)
        {
            try
            {
                var result=await TelemetryRetention.Run(db,token);
                if(result.Enabled && (result.RfHours>0 || result.InterfaceHours>0 ||
                    result.RfRawDeleted>0 || result.InterfaceRawDeleted>0))
                    logger.LogInformation("Telemetry rollups: {RfHours} RF, {InterfaceHours} IF-MIB; old raw pruned: {RfRaw}/{InterfaceRaw}",
                        result.RfHours,result.InterfaceHours,
                        result.RfRawDeleted,result.InterfaceRawDeleted);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception error){logger.LogError(error,"Telemetry retention failed; no partial deletion committed");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
}
