// root/selfhost/src/NetForge.Api/ServiceAssurance.cs
// Subscriber assurance reports make no contractual or automatic credit claims.
// PPPoE accounting is observation; downtime requires explicit staff attestation.
using System.Globalization;
using Npgsql;

namespace NetForge.Api;

public sealed record AssurancePolicyInput(decimal TargetPercent,int AccountingFreshnessMinutes,bool Confirm);
public sealed record AssuranceInterruptionInput(
    Guid ServiceId,Guid? IncidentId,Guid? TicketId,
    DateTimeOffset StartedAt,DateTimeOffset EndedAt,
    string Evidence,bool Confirm);

public static class ServiceAssurance
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Staff(HttpContext ctx)=>ctx.User.IsInRole("admin")||ctx.User.IsInRole("dispatcher");
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static IResult Denied()=>Results.Problem("NOC staff permissions required",statusCode:403);
    private static IResult Invalid(string value)=>Results.BadRequest(new{error=value});
    private static IResult Conflict(string value)=>Results.Conflict(new{error=value});
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/assurance/policy",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            var settings=await Store.Rows(db,"""
                SELECT target_percent,accounting_freshness_minutes,updated_at
                FROM assurance_settings WHERE id=1
                """);
            return Results.Ok(new{
                settings=settings.Single(),
                interpretation="Internal availability threshold used as an evidence-screening budget, not a contractual SLA guarantee."
            });
        });
        api.MapPut("/assurance/policy",async(HttpContext ctx,NpgsqlDataSource db,
            AssurancePolicyInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm || input.TargetPercent is <90m or >99.999m ||
               input.AccountingFreshnessMinutes is <2 or >120)
                return Invalid("Confirm target 90.000–99.999 percent and RADIUS freshness 2–120 minutes");
            await Store.Execute(db,"""
                UPDATE assurance_settings SET target_percent=@target,
                    accounting_freshness_minutes=@fresh,updated_at=now() WHERE id=1
                """,P("target",input.TargetPercent),P("fresh",input.AccountingFreshnessMinutes));
            await Store.Audit(db,Actor(ctx),"assurance-policy","assurance_settings","1");
            return Results.Ok(new{ok=true,
                note="Target is an internal screening threshold only; no automatic credits or contract compliance claims."});
        });

        api.MapGet("/assurance/reports/monthly",async(HttpContext ctx,
            NpgsqlDataSource db,string? period,Guid? customerId)=>
        {
            if(!Staff(ctx))return Denied();
            DateTime start;
            if(string.IsNullOrWhiteSpace(period))
            {
                var now=DateTime.UtcNow;
                start=new DateTime(now.Year,now.Month,1,0,0,0,DateTimeKind.Utc);
            }
            else if(DateOnly.TryParseExact(period,"yyyy-MM",CultureInfo.InvariantCulture,
                DateTimeStyles.None,out var month))
                start=new DateTime(month.Year,month.Month,1,0,0,0,DateTimeKind.Utc);
            else
                return Invalid("Use period=YYYY-MM");
            var end=start.AddMonths(1);
            if(start>DateTime.UtcNow)
                return Invalid("Future reporting periods cannot be scored");
            var policy=(await Store.Rows(db,"""
                SELECT target_percent FROM assurance_settings WHERE id=1
                """)).Single();
            var target=(decimal)policy["target_percent"]!;
            var rows=await Store.Rows(db,"""
                WITH eligible AS (
                    SELECT s.id AS service_id,s.customer_id,c.account,
                        c.first_name,c.surname,s.username,s.state,
                        s.created_at,s.changed_at,
                        GREATEST(s.created_at,@period_start::timestamptz) AS window_start,
                        LEAST(@period_end::timestamptz,now()) AS window_end
                    FROM services s JOIN customers c ON c.id=s.customer_id
                    WHERE (@customer::uuid IS NULL OR s.customer_id=@customer)
                ), intervals AS (
                    SELECT e.*,
                      GREATEST(
                        EXTRACT(EPOCH FROM e.window_end-e.window_start),0
                      )::numeric AS period_seconds,
                      COALESCE((
                        SELECT SUM(EXTRACT(EPOCH FROM
                            LEAST(i.ended_at,e.window_end)-
                            GREATEST(i.started_at,e.window_start)))
                        FROM assurance_interruptions i
                        WHERE i.service_id=e.service_id
                          AND i.started_at<e.window_end
                          AND i.ended_at>e.window_start
                      ),0)::numeric AS attested_seconds,
                      (SELECT count(*) FROM assurance_interruptions i
                       WHERE i.service_id=e.service_id
                         AND i.started_at<e.window_end
                         AND i.ended_at>e.window_start) AS interruption_count,
                      (SELECT count(*) FROM radacct r
                       WHERE r.username=e.username
                         AND r.acctstarttime<e.window_end
                         AND COALESCE(r.acctstoptime,r.acctupdatetime,r.acctstarttime)
                             >=e.window_start) AS accounting_sessions,
                      (SELECT count(*) FROM radacct r
                       WHERE r.username=e.username AND r.acctstoptime IS NOT NULL
                         AND r.acctstoptime>=e.window_start
                         AND r.acctstoptime<e.window_end) AS recorded_stops
                    FROM eligible e
                )
                SELECT service_id,customer_id,account,first_name,surname,
                    username,state AS current_service_state,created_at,
                    window_start,window_end,
                    round(period_seconds/60,2) AS observation_window_minutes,
                    round(attested_seconds/60,2) AS staff_attested_downtime_minutes,
                    interruption_count,accounting_sessions,recorded_stops,
                    round(period_seconds*(100-@target::numeric)/100/60,2)
                        AS internal_threshold_budget_minutes,
                    CASE WHEN period_seconds>0 THEN
                        round((period_seconds-attested_seconds)*100/period_seconds,4)
                        ELSE NULL END AS maximum_possible_availability_percent,
                    CASE WHEN period_seconds<=0 THEN 'outside_observation_window'
                         WHEN attested_seconds>period_seconds*(100-@target::numeric)/100
                           THEN 'documented_threshold_exceeded'
                         ELSE 'insufficient_evidence_for_compliance' END AS finding
                FROM intervals
                ORDER BY account,username LIMIT 10000
                """,P("period_start",start),P("period_end",end),
                P("customer",customerId),P("target",target));
            var exceeded=rows.Count(x=>x["finding"] is "documented_threshold_exceeded");
            return Results.Ok(new{
                period=start.ToString("yyyy-MM",CultureInfo.InvariantCulture),
                timezone="UTC",targetPercent=target,
                reportingCutoff=end<DateTime.UtcNow?end:DateTime.UtcNow,
                services=rows,documentedBudgetExceeded=exceeded,
                classification="INTERNAL EVIDENCE REPORT — NOT CONTRACTUAL SLA CERTIFICATION",
                methodology="Only staff-attested, source-linked intervals count as documented downtime. No record does not mean verified availability. RADIUS accounting session observations/stops are diagnostic signals and never auto-converted into outage minutes. Availability is an upper ceiling assuming no unrecorded outages. Current service state is not a historical contract/service-activation ledger."
            });
        });

        api.MapGet("/assurance/interruptions",async(HttpContext ctx,
            NpgsqlDataSource db,Guid? serviceId,Guid? incidentId)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT i.id,i.service_id,s.username,c.account,
                    i.incident_id,i.ticket_id,i.started_at,i.ended_at,
                    round(EXTRACT(EPOCH FROM i.ended_at-i.started_at)/60,2)
                        AS documented_minutes,
                    i.evidence,i.source,i.verified_by,i.verified_at
                FROM assurance_interruptions i
                JOIN services s ON s.id=i.service_id
                JOIN customers c ON c.id=s.customer_id
                WHERE (@service::uuid IS NULL OR i.service_id=@service)
                  AND (@incident::uuid IS NULL OR i.incident_id=@incident)
                ORDER BY i.started_at DESC LIMIT 2000
                """,P("service",serviceId),P("incident",incidentId)));
        });

        api.MapPost("/assurance/interruptions",async(HttpContext ctx,
            NpgsqlDataSource db,AssuranceInterruptionInput input)=>
        {
            if(!Staff(ctx))return Denied();
            var explanation=(input.Evidence??"").Trim();
            var start=input.StartedAt.ToUniversalTime().UtcDateTime;
            var end=input.EndedAt.ToUniversalTime().UtcDateTime;
            var now=DateTime.UtcNow;
            if(!input.Confirm || input.ServiceId==Guid.Empty ||
               (input.IncidentId is null && input.TicketId is null) ||
               explanation.Length is <20 or >2000 || start>=end ||
               end>now.AddMinutes(1) || start>now || end-start>TimeSpan.FromDays(90))
                return Invalid("Confirm a documented past interruption (up to 90 days) with a source incident/ticket and evidence of 20–2000 characters");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var services=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT s.id,s.customer_id,s.created_at,c.highsite_id
                FROM services s JOIN customers c ON c.id=s.customer_id
                WHERE s.id=@id FOR UPDATE OF s
                """,P("id",input.ServiceId));
            if(services.Count==0)return Results.NotFound();
            var service=services.Single();
            if(start<(DateTime)service["created_at"]!)
                return Conflict("Interruption predates the service creation timestamp");
            if(input.IncidentId is Guid incident)
            {
                var membership=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                    SELECT s.incident_id FROM assurance_incident_services s
                    WHERE s.incident_id=@incident AND s.service_id=@service
                    """,P("incident",incident),P("service",input.ServiceId));
                if(membership.Count==0)
                    return Conflict("This service was not recorded in that incident's original exposure snapshot");
            }
            if(input.TicketId is Guid ticket)
            {
                var matches=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                    SELECT id FROM tickets WHERE id=@ticket AND (
                        customer_id=@customer OR
                        (type='highsite' AND highsite_id=@site))
                    """,P("ticket",ticket),P("customer",service["customer_id"]),
                    P("site",service["highsite_id"]));
                if(matches.Count==0)
                    return Conflict("Ticket does not belong to the subscriber or its recorded highsite");
            }
            var overlap=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT id FROM assurance_interruptions
                WHERE service_id=@service
                    AND tstzrange(started_at,ended_at,'[)') &&
                        tstzrange(@start::timestamptz,@end::timestamptz,'[)')
                LIMIT 1
                """,P("service",input.ServiceId),P("start",start),P("end",end));
            if(overlap.Count>0)return Conflict("Interruption overlaps an existing staff-attested interval");
            var created=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO assurance_interruptions(service_id,incident_id,
                    ticket_id,started_at,ended_at,evidence,verified_by)
                VALUES(@service,@incident,@ticket,@start,@end,@evidence,@actor)
                RETURNING id
                """,P("service",input.ServiceId),P("incident",input.IncidentId),
                P("ticket",input.TicketId),P("start",start),P("end",end),
                P("evidence",explanation),P("actor",Actor(ctx)));
            var id=(Guid)created.Single()["id"]!;
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "assurance-interruption-attested","assurance_interruption",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/assurance/interruptions/"+id,
                new{id,source="staff_attested",note=
                    "Staff-attested interval recorded; independent verification and contract review remain required for SLA credits."});
        });
        AssuranceEvidence.Map(api);
    }
}
