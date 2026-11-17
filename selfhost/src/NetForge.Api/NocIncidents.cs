// root/selfhost/src/NetForge.Api/NocIncidents.cs
// Site monitoring creates potential incidents; customer loss is NOT verified.
// No incident transition can alter RADIUS access, billing or customer messaging.
using Npgsql;

namespace NetForge.Api;

public sealed record NocManualInput(Guid HighsiteId,string Summary,string Severity,bool Confirm);
public static class NocIncidents
{
    internal static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    internal static bool Staff(HttpContext ctx)=>ctx.User.IsInRole("admin")||ctx.User.IsInRole("dispatcher");
    internal static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    internal static IResult Denied()=>Results.Problem("NOC staff permissions required",statusCode:403);
    internal static IResult Invalid(string reason)=>Results.BadRequest(new{error=reason});
    internal static IResult Conflict(string reason)=>Results.Conflict(new{error=reason});

    internal static Task History(NpgsqlConnection conn,NpgsqlTransaction tx,
        Guid incident,string action,string actor,string detail)=>
        NetworkManagement.Execute(conn,tx,"""
            INSERT INTO noc_incident_history(incident_id,action,actor,detail)
            VALUES(@id,@action,@actor,@detail)
            """,P("id",incident),P("action",action),P("actor",actor),
            P("detail",detail.Length>2000?detail[..2000]:detail));

    internal static async Task Snapshot(NpgsqlConnection conn,NpgsqlTransaction tx,
        Guid incident,Guid highsite)
    {
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO noc_incident_customers(
                incident_id,customer_id,account_snapshot,first_name_snapshot,
                surname_snapshot,active_services)
            SELECT @incident,c.id,c.account,c.first_name,c.surname,
                (SELECT count(*)::integer FROM services s
                 WHERE s.customer_id=c.id AND s.state='active')
            FROM customers c WHERE c.highsite_id=@site AND c.status='active'
            ON CONFLICT(incident_id,customer_id) DO NOTHING
            """,P("incident",incident),P("site",highsite));
        await NetworkManagement.Execute(conn,tx,"""
            UPDATE noc_incidents SET
              potential_customers=(SELECT count(*) FROM noc_incident_customers
                 WHERE incident_id=@incident),
              sectors_snapshot=(SELECT count(*) FROM sectors WHERE highsite_id=@site)
            WHERE id=@incident
            """,P("incident",incident),P("site",highsite));
    }

    // Called inside SiteImpact's serializable-by-site transaction. A second
    // upstream failure on the same site never creates another active incident.
    internal static async Task OnUpstreamTransition(NpgsqlConnection conn,
        NpgsqlTransaction tx,Guid eventId,Guid site,string siteName,
        string transition,string trigger)
    {
        var open=await NetworkManagement.Query(conn,tx,"""
            SELECT id,status,recovered_at FROM noc_incidents
            WHERE highsite_id=@site AND status!='resolved' FOR UPDATE
            """,P("site",site));
        if(transition=="down")
        {
            if(open.Count==0)
            {
                var title="Potential upstream outage: "+siteName;
                if(title.Length>200)title=title[..200];
                var created=await NetworkManagement.Query(conn,tx,"""
                    INSERT INTO noc_incidents(highsite_id,source_event_id,
                        origin,summary,severity)
                    VALUES(@site,@event,'monitor',@summary,'high') RETURNING id
                    """,P("site",site),P("event",eventId),P("summary",title));
                var id=(Guid)created.Single()["id"]!;
                await Snapshot(conn,tx,id,site);
                await History(conn,tx,id,"detected","monitor",
                    "Critical upstream TCP target DOWN: "+trigger+
                    ". Customer interruption remains unverified.");
            }
            else
            {
                var current=open.Single();
                var id=(Guid)current["id"]!;
                if(current["recovered_at"] is not null)
                {
                    await NetworkManagement.Execute(conn,tx,"""
                        UPDATE noc_incidents SET recovered_at=NULL,
                            status=CASE WHEN status='monitoring' THEN 'open'
                                ELSE status END,
                            updated_at=now(),revision=revision+1 WHERE id=@id
                        """,P("id",id));
                    await History(conn,tx,id,"upstream_failed_again","monitor",
                        "Potential upstream problem recurred before NOC resolution: "+trigger);
                }
            }
            return;
        }
        if(transition=="recovered" && open.Count>0)
        {
            var row=open.Single();
            if(row["recovered_at"] is not null)return;
            var id=(Guid)row["id"]!;
            await NetworkManagement.Execute(conn,tx,"""
                UPDATE noc_incidents SET recovered_at=now(),
                    status='monitoring',updated_at=now(),revision=revision+1
                WHERE id=@id AND status!='resolved'
                """,P("id",id));
            await History(conn,tx,id,"upstream_recovered","monitor",
                "All tagged upstream TCP monitors recovered. Staff must confirm resolution.");
        }
    }

    internal static Task SiteLock(NpgsqlConnection conn,NpgsqlTransaction tx,Guid site)=>
        NetworkManagement.Execute(conn,tx,
            "SELECT pg_advisory_xact_lock(7492,hashtext(@site))",
            P("site",site.ToString()));

    internal static Task<List<Dictionary<string,object?>>> LockIncident(
        NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)=>
        NetworkManagement.Query(conn,tx,
            "SELECT id,highsite_id,origin,status,recovered_at FROM noc_incidents WHERE id=@id FOR UPDATE",
            P("id",id));

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/noc/summary",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            var rows=await Store.Rows(db,"""
                SELECT count(*) FILTER(WHERE status='open') AS open,
                    count(*) FILTER(WHERE status='acknowledged') AS acknowledged,
                    count(*) FILTER(WHERE status='monitoring') AS monitoring,
                    count(*) FILTER(WHERE status='resolved') AS resolved,
                    COALESCE(sum(potential_customers) FILTER(
                        WHERE status!='resolved'),0) AS potential_customer_exposure
                FROM noc_incidents
                """);
            return Results.Ok(new{summary=rows.Single(),
                note="Exposure counts are inventory snapshots, not verified outages or contractual SLA figures."});
        });
        api.MapGet("/noc/incidents",async(HttpContext ctx,NpgsqlDataSource db,string? state)=>
        {
            if(!Staff(ctx))return Denied();
            if(state is not (null or "active" or "resolved" or "all"))
                return Invalid("Use state=active, resolved or all");
            return Results.Ok(await Store.Rows(db,"""
                SELECT i.id,i.highsite_id,h.name AS highsite,i.origin,
                    i.summary,i.severity,i.status,i.opened_at,i.recovered_at,
                    i.resolved_at,i.acknowledged_at,i.acknowledged_by,
                    i.assigned_to,i.resolved_by,i.resolution_note,
                    i.potential_customers,i.sectors_snapshot,i.revision,
                    l.ticket_id,t.number AS ticket_number,
                    EXTRACT(EPOCH FROM
                        (COALESCE(i.recovered_at,i.resolved_at,now())-i.opened_at)
                    )/60 AS observed_minutes
                FROM noc_incidents i JOIN highsites h ON h.id=i.highsite_id
                LEFT JOIN noc_incident_tickets l ON l.incident_id=i.id
                LEFT JOIN tickets t ON t.id=l.ticket_id
                WHERE (@filter::text='all' OR
                       (@filter='resolved' AND i.status='resolved') OR
                       (@filter='active' AND i.status!='resolved'))
                ORDER BY i.opened_at DESC,i.id LIMIT 1500
                """,P("filter",state??"active")));
        });
        api.MapGet("/noc/incidents/{id:guid}",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            var rows=await Store.Rows(db,"""
                SELECT i.*,h.name AS highsite,l.ticket_id,t.number AS ticket_number
                FROM noc_incidents i JOIN highsites h ON h.id=i.highsite_id
                LEFT JOIN noc_incident_tickets l ON l.incident_id=i.id
                LEFT JOIN tickets t ON t.id=l.ticket_id
                WHERE i.id=@id
                """,P("id",id));
            return rows.Count==0?Results.NotFound():Results.Ok(rows.Single());
        });
        api.MapGet("/noc/incidents/{id:guid}/customers",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,int? offset,int? limit)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000 || limit is <1 or >500)
                return Invalid("offset 0–1000000 and limit 1–500 required");
            var known=await Store.Rows(db,"SELECT id FROM noc_incidents WHERE id=@id",P("id",id));
            if(known.Count==0)return Results.NotFound();
            var rows=await Store.Rows(db,"""
                SELECT s.customer_id,s.account_snapshot,s.first_name_snapshot,
                    s.surname_snapshot,s.active_services,c.status AS current_status,
                    c.highsite_id AS current_site
                FROM noc_incident_customers s
                JOIN customers c ON c.id=s.customer_id
                WHERE s.incident_id=@id ORDER BY s.account_snapshot
                LIMIT @limit OFFSET @offset
                """,P("id",id),P("offset",offset??0),P("limit",limit??100));
            return Results.Ok(new{customers=rows,offset=offset??0,limit=limit??100,
                note="Potentially exposed subscribers at incident start, not confirmed disconnected."});
        });
        api.MapGet("/noc/incidents/{id:guid}/history",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            var known=await Store.Rows(db,"SELECT id FROM noc_incidents WHERE id=@id",P("id",id));
            if(known.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT id,action,actor,detail,occurred_at
                FROM noc_incident_history WHERE incident_id=@id
                ORDER BY occurred_at,id LIMIT 2000
                """,P("id",id)));
        });
        api.MapPost("/noc/incidents",async(HttpContext ctx,NpgsqlDataSource db,
            NocManualInput input)=>
        {
            if(!Staff(ctx))return Denied();
            if(!input.Confirm || input.HighsiteId==Guid.Empty ||
                (input.Summary??"").Trim().Length is <5 or >200 ||
                input.Severity is not ("low" or "normal" or "high" or "critical"))
                return Invalid("Select highsite, summary 5–200 characters, severity and confirm");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await SiteLock(conn,(NpgsqlTransaction)tx,input.HighsiteId);
            var highsite=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                "SELECT id FROM highsites WHERE id=@site",P("site",input.HighsiteId));
            if(highsite.Count==0)return Results.NotFound();
            var exists=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                "SELECT id FROM noc_incidents WHERE highsite_id=@site AND status!='resolved'",
                P("site",input.HighsiteId));
            if(exists.Count>0)return Conflict("A NOC incident is already active for that highsite");
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO noc_incidents(highsite_id,origin,summary,severity)
                VALUES(@site,'manual',@summary,@severity) RETURNING id
                """,P("site",input.HighsiteId),P("summary",input.Summary!.Trim()),
                P("severity",input.Severity));
            var id=(Guid)rows.Single()["id"]!;
            await Snapshot(conn,(NpgsqlTransaction)tx,id,input.HighsiteId);
            await History(conn,(NpgsqlTransaction)tx,id,"manual_open",Actor(ctx),
                "Operator-reported possible outage. No automatic customer notification.");
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "noc-create","noc_incident",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/noc/incidents/"+id,new{id});
        });
        NocWorkflow.Map(api);
    }
}
