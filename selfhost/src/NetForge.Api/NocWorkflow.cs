// root/selfhost/src/NetForge.Api/NocWorkflow.cs
// Explicit human triage actions, staff assignment, immutable notes and CRM links.
// Do not automatically suspend customers or send customer-facing messages.
using Npgsql;

namespace NetForge.Api;

public sealed record NocNoteInput(string Note);
public sealed record NocResolveInput(string Resolution,bool Confirm);
public sealed record NocAssignInput(string? Username);
public sealed record NocTicketInput(Guid? TicketId,string? Subject,string? Description,bool Confirm);

public static class NocWorkflow
{
    private static NpgsqlParameter P(string name,object? value)=>NocIncidents.P(name,value);
    private static IResult Denied()=>NocIncidents.Denied();
    private static IResult Invalid(string value)=>NocIncidents.Invalid(value);
    private static IResult Conflict(string value)=>NocIncidents.Conflict(value);
    private static string Actor(HttpContext ctx)=>NocIncidents.Actor(ctx);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/noc/incidents/{id:guid}/acknowledge",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!NocIncidents.Staff(ctx))return Denied();
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NocIncidents.LockIncident(conn,(NpgsqlTransaction)tx,id);
            if(rows.Count==0)return Results.NotFound();
            if((string)rows.Single()["status"]! != "open")
                return Conflict("Only open incidents can be acknowledged");
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE noc_incidents SET status='acknowledged',
                    acknowledged_by=@actor,acknowledged_at=now(),
                    updated_at=now(),revision=revision+1 WHERE id=@id
                """,P("id",id),P("actor",Actor(ctx)));
            await NocIncidents.History(conn,(NpgsqlTransaction)tx,id,
                "acknowledged",Actor(ctx),"Operator acknowledged incident");
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "noc-acknowledge","noc_incident",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,status="acknowledged"});
        });

        api.MapPost("/noc/incidents/{id:guid}/notes",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,NocNoteInput input)=>
        {
            if(!NocIncidents.Staff(ctx))return Denied();
            var note=(input.Note??"").Trim();
            if(note.Length is <3 or >2000)
                return Invalid("Internal note must contain 3–2000 characters");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NocIncidents.LockIncident(conn,(NpgsqlTransaction)tx,id);
            if(rows.Count==0)return Results.NotFound();
            await NocIncidents.History(conn,(NpgsqlTransaction)tx,id,
                "note",Actor(ctx),note);
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
                "UPDATE noc_incidents SET updated_at=now(),revision=revision+1 WHERE id=@id",
                P("id",id));
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,note="Internal NOC note appended; not sent to customer portal."});
        });

        api.MapPost("/noc/incidents/{id:guid}/assign",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,NocAssignInput input)=>
        {
            if(!NocIncidents.Staff(ctx))return Denied();
            var username=(input.Username??"").Trim();
            if(username.Length>80)return Invalid("Assignee username too long");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NocIncidents.LockIncident(conn,(NpgsqlTransaction)tx,id);
            if(rows.Count==0)return Results.NotFound();
            if((string)rows.Single()["status"]! == "resolved")
                return Conflict("Resolved incidents cannot be reassigned");
            if(username.Length>0)
            {
                var matches=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                    SELECT username FROM app_users
                    WHERE lower(username)=lower(@username) AND enabled=true
                        AND role IN ('admin','dispatcher','technician')
                    """,P("username",username));
                if(matches.Count==0)return Conflict("Assignee must be an enabled technician or NOC user");
                username=(string)matches.Single()["username"]!;
            }
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE noc_incidents SET assigned_to=@username,updated_at=now(),
                    revision=revision+1 WHERE id=@id
                """,P("id",id),P("username",username.Length>0?username:null));
            await NocIncidents.History(conn,(NpgsqlTransaction)tx,id,
                "assigned",Actor(ctx),
                username.Length>0?"Assigned to "+username:"Assignment cleared");
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "noc-assign","noc_incident",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,assignedTo=username});
        });

        api.MapPost("/noc/incidents/{id:guid}/resolve",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,NocResolveInput input)=>
        {
            if(!NocIncidents.Staff(ctx))return Denied();
            var reason=(input.Resolution??"").Trim();
            if(!input.Confirm || reason.Length is <8 or >2000)
                return Invalid("Confirm with an internal resolution reason of 8–2000 characters");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NocIncidents.LockIncident(conn,(NpgsqlTransaction)tx,id);
            if(rows.Count==0)return Results.NotFound();
            var incident=rows.Single();
            if((string)incident["status"]! == "resolved")
                return Conflict("Incident already resolved");
            if((string)incident["origin"]! == "monitor" &&
               incident["recovered_at"] is null)
                return Conflict("The tagged upstream must recover before resolving a detected incident");
            if(await SiteImpact.IsSuppressed(conn,(NpgsqlTransaction)tx,
                (Guid)incident["highsite_id"]!))
                return Conflict("A critical upstream TCP monitor remains DOWN");
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE noc_incidents SET status='resolved',resolved_at=now(),
                    resolved_by=@actor,resolution_note=@reason,
                    updated_at=now(),revision=revision+1 WHERE id=@id
                """,P("id",id),P("actor",Actor(ctx)),P("reason",reason));
            await NocIncidents.History(conn,(NpgsqlTransaction)tx,id,
                "resolved",Actor(ctx),reason);
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "noc-resolve","noc_incident",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,status="resolved",
                note="Resolved by staff. No customer account, billing or RADIUS change."});
        });

        api.MapPost("/noc/incidents/{id:guid}/ticket",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,NocTicketInput input)=>
        {
            if(!NocIncidents.Staff(ctx))return Denied();
            if(!input.Confirm || (input.Subject??"").Length>200 ||
               (input.Description??"").Length>2000)
                return Invalid("Confirm the ticket link, subject <=200 and description <=2000");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT i.id,i.summary,i.severity,i.highsite_id,
                    l.ticket_id FROM noc_incidents i
                LEFT JOIN noc_incident_tickets l ON l.incident_id=i.id
                WHERE i.id=@id FOR UPDATE OF i
                """,P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var incident=rows.Single();
            if(incident["ticket_id"] is not null)
                return Conflict("Incident already has a CRM ticket");
            var site=(Guid)incident["highsite_id"]!;
            Guid ticket;
            if(input.TicketId is Guid existing)
            {
                var linked=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                    SELECT id FROM tickets
                    WHERE id=@id AND highsite_id=@site AND type='highsite'
                    """,P("id",existing),P("site",site));
                if(linked.Count==0)
                    return Conflict("Existing ticket must be a highsite ticket for this incident's site");
                ticket=existing;
            }
            else
            {
                var title=(input.Subject??"").Trim();
                if(title.Length<5)
                {
                    title="NOC: "+(string)incident["summary"]!;
                    if(title.Length>200)title=title[..200];
                }
                var description=(input.Description??"").Trim();
                if(description.Length==0)
                    description="Internal NOC incident "+id+
                        ". Potential highsite service disruption. Verify actual customer impact.";
                var priority=(string)incident["severity"]! is "critical"?"critical":
                    (string)incident["severity"]! is "high"?"high":"normal";
                var created=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                    INSERT INTO tickets(highsite_id,subject,description,type,priority)
                    VALUES(@site,@subject,@description,'highsite',@priority)
                    RETURNING id
                    """,P("site",site),P("subject",title),
                    P("description",description),P("priority",priority));
                ticket=(Guid)created.Single()["id"]!;
                await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                    "create","ticket",ticket.ToString());
            }
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO noc_incident_tickets(incident_id,ticket_id,linked_by)
                VALUES(@incident,@ticket,@actor)
                """,P("incident",id),P("ticket",ticket),P("actor",Actor(ctx)));
            await NocIncidents.History(conn,(NpgsqlTransaction)tx,id,
                "ticket_linked",Actor(ctx),"Linked CRM highsite ticket "+ticket);
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "noc-ticket-link","noc_incident",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,ticketId=ticket});
        });
    }
}
