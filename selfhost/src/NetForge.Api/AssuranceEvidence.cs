// root/selfhost/src/NetForge.Api/AssuranceEvidence.cs
// Persist contemporaneous RADIUS accounting observations with NOC incidents.
// A stale session or recorded stop is evidence, NEVER proof of subscriber outage.
using Npgsql;

namespace NetForge.Api;

public sealed record AssuranceRefreshInput(bool Confirm);

public static class AssuranceEvidence
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Staff(HttpContext ctx)=>ctx.User.IsInRole("admin")||
        ctx.User.IsInRole("dispatcher");
    private static IResult Denied()=>Results.Problem("NOC staff permissions required",statusCode:403);

    public static async Task SnapshotIncident(NpgsqlConnection conn,
        NpgsqlTransaction tx,Guid incidentId)
    {
        // This is called only on incident creation, after NOC customer
        // exposure has been snapshotted. Later site/customer changes never
        // insert a newly assigned subscriber into this incident.
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO assurance_incident_services(
                incident_id,service_id,customer_id,username_snapshot,service_state_snapshot)
            SELECT @incident,s.id,s.customer_id,s.username,s.state
            FROM services s JOIN noc_incident_customers snapshot
              ON snapshot.customer_id=s.customer_id
             AND snapshot.incident_id=@incident
            WHERE s.state='active'
            ON CONFLICT(incident_id,service_id) DO NOTHING
            """,P("incident",incidentId));
        await Capture(conn,tx,incidentId);
    }

    // One INSERT..SELECT statement captures every service against the same
    // current PostgreSQL snapshot, avoiding per-subscriber query storms.
    // Recent activity means a fresh Acct-Interim-Update or Acct-Start,
    // not verified payload forwarding or internet availability.
    internal static async Task<int> Capture(NpgsqlConnection conn,NpgsqlTransaction tx,
        Guid incidentId)
    {
        var rows=await NetworkManagement.Query(conn,tx,"""
            WITH captured AS (
            INSERT INTO assurance_session_observations(
                incident_id,service_id,state,open_sessions,recent_open_sessions,
                last_accounting_at,last_session_stop_at,stop_reason)
            SELECT membership.incident_id,membership.service_id,
                CASE WHEN recent.open_count>0 THEN 'recent_accounting'
                     WHEN recent.all_count>0 THEN 'stale_accounting'
                     WHEN recent.last_stop_at>=now()-make_interval(mins=>p.accounting_freshness_minutes)
                          THEN 'recent_stop_observed'
                     ELSE 'no_accounting_data' END,
                recent.all_count,recent.open_count,recent.last_open_at,
                recent.last_stop_at,stopped.acctterminatecause
            FROM assurance_incident_services membership
            CROSS JOIN assurance_settings p
            LEFT JOIN LATERAL (
                SELECT
                    count(*) FILTER(WHERE r.acctstoptime IS NULL)::integer AS all_count,
                    count(*) FILTER(WHERE r.acctstoptime IS NULL AND
                        COALESCE(r.acctupdatetime,r.acctstarttime)>=
                        now()-make_interval(mins=>p.accounting_freshness_minutes))::integer
                        AS open_count,
                    max(COALESCE(r.acctupdatetime,r.acctstarttime))
                        FILTER(WHERE r.acctstoptime IS NULL) AS last_open_at,
                    max(r.acctstoptime) AS last_stop_at
                FROM radacct r WHERE r.username=membership.username_snapshot
            ) recent ON true
            LEFT JOIN LATERAL (
                SELECT left(r.acctterminatecause,160) AS acctterminatecause
                FROM radacct r
                WHERE r.username=membership.username_snapshot
                  AND r.acctstoptime IS NOT NULL
                ORDER BY r.acctstoptime DESC,r.radacctid DESC LIMIT 1
            ) stopped ON true
            WHERE membership.incident_id=@incident
            RETURNING 1
            )
            SELECT count(*) AS total FROM captured
            """,P("incident",incidentId));
        return checked((int)(long)rows.Single()["total"]!);
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/assurance/incidents/{id:guid}/evidence",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,int? offset,int? limit)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000 || limit is <1 or >500)
                return Results.BadRequest(new{error="offset 0–1000000; limit 1–500"});
            var known=await Store.Rows(db,"SELECT id FROM noc_incidents WHERE id=@id",P("id",id));
            if(known.Count==0)return Results.NotFound();
            var rows=await Store.Rows(db,"""
                SELECT a.service_id,a.customer_id,a.username_snapshot,
                    a.service_state_snapshot,
                    c.account,
                    current.state AS evidence_state,
                    current.observed_at,current.open_sessions,
                    current.recent_open_sessions,current.last_accounting_at,
                    current.last_session_stop_at,current.stop_reason
                FROM assurance_incident_services a
                JOIN customers c ON c.id=a.customer_id
                LEFT JOIN LATERAL (
                    SELECT e.state,e.observed_at,e.open_sessions,
                        e.recent_open_sessions,e.last_accounting_at,
                        e.last_session_stop_at,e.stop_reason
                    FROM assurance_session_observations e
                    WHERE e.incident_id=a.incident_id AND e.service_id=a.service_id
                    ORDER BY e.observed_at DESC,e.id DESC LIMIT 1
                ) current ON true
                WHERE a.incident_id=@id
                ORDER BY c.account,a.username_snapshot
                LIMIT @limit OFFSET @offset
                """,P("id",id),P("offset",offset??0),P("limit",limit??100));
            var count=await Store.Rows(db,
                "SELECT count(*) AS total FROM assurance_incident_services WHERE incident_id=@id",
                P("id",id));
            return Results.Ok(new{
                incidentId=id,
                total=count.Single()["total"],
                offset=offset??0,
                limit=limit??100,
                records=rows,
                interpretation="RADIUS accounting is a sampled session signal. Recent/stale/stop/no data do not independently prove internet reachability or contract downtime."
            });
        });

        api.MapPost("/assurance/incidents/{id:guid}/refresh",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,AssuranceRefreshInput input)=>
        {
            if(!Staff(ctx))return Denied();
            if(!input.Confirm)
                return Results.BadRequest(new{error="Confirm collecting a fresh RADIUS accounting snapshot"});
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var incident=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                "SELECT id FROM noc_incidents WHERE id=@id FOR UPDATE",P("id",id));
            if(incident.Count==0)return Results.NotFound();
            var captured=await Capture(conn,(NpgsqlTransaction)tx,id);
            await NocIncidents.History(conn,(NpgsqlTransaction)tx,id,
                "radius_evidence",ctx.User.Identity?.Name??"unknown",
                "Captured "+captured+" additional contemporaneous RADIUS account snapshots");
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,captured,
                note="Accounting evidence refreshed; historical observations retained without recalculating outage claims."});
        });

        api.MapGet("/assurance/incidents/{id:guid}/evidence/{serviceId:guid}/history",
            async(Guid id,Guid serviceId,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            var members=await Store.Rows(db,"""
                SELECT service_id FROM assurance_incident_services
                WHERE incident_id=@incident AND service_id=@service
                """,P("incident",id),P("service",serviceId));
            if(members.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT observed_at,state,open_sessions,recent_open_sessions,
                    last_accounting_at,last_session_stop_at,stop_reason,source
                FROM assurance_session_observations
                WHERE incident_id=@incident AND service_id=@service
                ORDER BY observed_at DESC,id DESC LIMIT 250
                """,P("incident",id),P("service",serviceId)));
        });
    }
}
