// root/selfhost/src/NetForge.Api/SiteImpact.cs
// Correlate explicitly tagged upstream TCP failures into potential highsite
// incidents. A failed TCP port is NOT proof that an entire tower is offline.
using Npgsql;

namespace NetForge.Api;

public sealed record MonitorSiteAssignment(Guid? HighsiteId,bool IsUpstream,bool Confirm);

public static class SiteImpact
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Staff(HttpContext ctx)=>ctx.User.IsInRole("admin")||ctx.User.IsInRole("dispatcher");
    private static IResult Denied()=>Results.Problem("Administrator permissions required",statusCode:403);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/monitor/sites",async (HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Results.Problem("Monitoring staff permissions required",statusCode:403);
            return Results.Ok(await Store.Rows(db,"""
                SELECT h.id,h.name,h.number,
                    COALESCE(m.monitored,0) AS monitored,
                    COALESCE(m.upstream,0) AS upstream,
                    COALESCE(m.upstream_down,0) AS upstream_down,
                    COALESCE(m.devices_down,0) AS devices_down,
                    COALESCE(c.customers,0) AS affected_customers,
                    COALESCE(s.sectors,0) AS sectors,
                    CASE WHEN COALESCE(m.upstream_down,0)>0
                        THEN 'potential_upstream_outage'
                        ELSE 'no_upstream_failure_recorded' END AS impact_state
                FROM highsites h
                LEFT JOIN LATERAL (
                    SELECT count(*) AS monitored,
                        count(*) FILTER(WHERE t.is_upstream) AS upstream,
                        count(*) FILTER(WHERE t.is_upstream AND t.state='down') AS upstream_down,
                        count(*) FILTER(WHERE t.state='down') AS devices_down
                    FROM monitor_targets t
                    WHERE t.highsite_id=h.id AND t.enabled=true
                ) m ON true
                LEFT JOIN LATERAL (
                    SELECT count(*) AS customers FROM customers c
                    WHERE c.highsite_id=h.id AND c.status='active'
                ) c ON true
                LEFT JOIN LATERAL (
                    SELECT count(*) AS sectors FROM sectors s WHERE s.highsite_id=h.id
                ) s ON true
                ORDER BY upstream_down DESC,affected_customers DESC,h.name
                """));
        });
        api.MapGet("/monitor/site-events",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Results.Problem("Monitoring staff permissions required",statusCode:403);
            return Results.Ok(await Store.Rows(db,"""
                SELECT e.id,e.highsite_id,h.name AS highsite,
                    e.transition,e.impacted_customers,e.sectors,
                    e.triggering_target_id,e.detail,e.occurred_at
                FROM site_monitor_events e JOIN highsites h ON h.id=e.highsite_id
                ORDER BY e.occurred_at DESC LIMIT 500
                """));
        });
        api.MapPut("/monitor/targets/{id:guid}/site",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,MonitorSiteAssignment input)=>
        {
            if(!ctx.User.IsInRole("admin"))return Denied();
            if(!input.Confirm || (input.IsUpstream && input.HighsiteId is null))
                return Results.BadRequest(new{
                    error="Explicit confirmation and a highsite are required for upstream impact monitoring"});
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT t.id,t.equipment_id,t.nas_id,t.highsite_id,
                    e.highsite_id AS equipment_site,c.highsite_id AS customer_site
                FROM monitor_targets t
                LEFT JOIN equipment e ON e.id=t.equipment_id
                LEFT JOIN customers c ON c.id=e.customer_id
                WHERE t.id=@id FOR UPDATE OF t
                """,P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var record=rows.Single();
            var known=record["equipment_site"] as Guid? ??
                record["customer_site"] as Guid?;
            if(known is not null && input.HighsiteId!=known)
                return Results.Conflict(new{
                    error="The chosen site does not match the asset's recorded highsite assignment"});
            if(input.HighsiteId is Guid newSite)
            {
                var found=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                    "SELECT id FROM highsites WHERE id=@id",P("id",newSite));
                if(found.Count==0)return Results.NotFound(new{error="Highsite not found"});
            }
            // Reset prior TCP health on topology changes; an existing DOWN from
            // another site must not generate an incident at the new site.
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE monitor_targets SET highsite_id=@site,is_upstream=@upstream,
                    state=CASE WHEN highsite_id IS DISTINCT FROM @site
                                 OR is_upstream IS DISTINCT FROM @upstream
                            THEN 'unknown' ELSE state END,
                    failure_streak=CASE WHEN highsite_id IS DISTINCT FROM @site
                                 OR is_upstream IS DISTINCT FROM @upstream
                            THEN 0 ELSE failure_streak END,
                    next_check_at=now() WHERE id=@id
                """,P("site",input.HighsiteId),P("upstream",input.IsUpstream),
                P("id",id));
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,
                ctx.User.Identity?.Name??"unknown","monitor-site-assignment",
                "monitor",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,note="Site assignment saved; health reset when topology changed."});
        });
    }

    public static async Task<bool> IsSuppressed(NpgsqlConnection conn,
        NpgsqlTransaction tx,Guid? site)
    {
        if(site is null)return false;
        var rows=await NetworkManagement.Query(conn,tx,"""
            SELECT EXISTS(
                SELECT 1 FROM monitor_targets
                WHERE highsite_id=@site AND enabled=true
                  AND is_upstream=true AND state='down'
            ) AS active
            """,P("site",site));
        return rows.Single()["active"] is true;
    }

    public static async Task HandleTcpTransition(
        NpgsqlConnection conn,NpgsqlTransaction tx,Guid id,
        Guid monitorEventId,Guid? highsite,bool upstream,string transition,
        string monitorName,string address,int port,
        IConfiguration configuration)
    {
        if(highsite is null)
        {
            await Notifications.EnqueueMonitorEvent(conn,tx,monitorEventId,
                "Monitor "+monitorName+" ("+address+":"+port+") is "+transition+
                ". TCP service reachability only; not confirmed subscriber outage.",
                configuration);
            return;
        }

        // Serialize per-highsite transitions from distinct target rows to
        // prevent duplicate site DOWN events during simultaneous checks.
        await NetworkManagement.Query(conn,tx,
            "SELECT pg_advisory_xact_lock(7492,hashtext(@site)) AS locked",
            P("site",highsite.Value.ToString()));
        var impacted=await IsSuppressed(conn,tx,highsite);
        if(upstream)
        {
            var other=await NetworkManagement.Query(conn,tx,"""
                SELECT count(*) AS count FROM monitor_targets
                WHERE highsite_id=@site AND enabled=true AND is_upstream=true
                  AND state='down'
                """,P("site",highsite));
            var downCount=(long)other.Single()["count"]!;
            var boundary=(transition=="down" && downCount==1) ||
                         (transition=="recovered" && downCount==0);
            await MarkSuppressed(conn,tx,monitorEventId);
            if(!boundary)return;
            var counts=await NetworkManagement.Query(conn,tx,"""
                SELECT h.name,
                    (SELECT count(*) FROM customers c
                     WHERE c.highsite_id=h.id AND c.status='active') AS customers,
                    (SELECT count(*) FROM sectors s
                     WHERE s.highsite_id=h.id) AS sectors
                FROM highsites h WHERE h.id=@site
                """,P("site",highsite));
            var snapshot=counts.Single();
            var countCustomers=checked((int)(long)snapshot["customers"]!);
            var countSectors=checked((int)(long)snapshot["sectors"]!);
            var siteEvent=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO site_monitor_events(highsite_id,triggering_target_id,
                    transition,impacted_customers,sectors,detail)
                VALUES(@site,@target,@transition,@customers,@sectors,@detail)
                RETURNING id
                """,P("site",highsite),P("target",id),P("transition",transition),
                P("customers",countCustomers),P("sectors",countSectors),
                P("detail","Tagged upstream TCP service "+monitorName+" "+transition));
            var siteEventId=(Guid)siteEvent.Single()["id"]!;
            await Notifications.EnqueueMonitorEvent(conn,tx,siteEventId,
                "Potential highsite upstream "+(transition=="down"?"OUTAGE":"RECOVERY")+
                ": "+snapshot["name"]+". Active customers potentially affected: "+
                countCustomers+"; sectors: "+countSectors+". Trigger: "+monitorName+
                ". TCP reachability alone does not confirm a site outage.",
                configuration);
            return;
        }

        if(impacted)
        {
            // Child devices are still recorded as DOWN/RECOVERED, but their
            // alerts are suppressed while a tagged upstream is DOWN.
            await MarkSuppressed(conn,tx,monitorEventId);
            return;
        }
        await Notifications.EnqueueMonitorEvent(conn,tx,monitorEventId,
            "Monitor "+monitorName+" ("+address+":"+port+") is "+transition+
            ". Highsite upstream has no recorded DOWN state.",
            configuration);
    }

    private static Task MarkSuppressed(NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)=>
        NetworkManagement.Execute(conn,tx,
            "UPDATE monitor_events SET suppressed=true WHERE id=@id",P("id",id));
}
