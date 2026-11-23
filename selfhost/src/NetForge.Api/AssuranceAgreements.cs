// root/selfhost/src/NetForge.Api/AssuranceAgreements.cs
// Immutable versioned internal SLA schedules; agreement refs are administrative
// evidence only, not authenticated signatures or contractual determinations.
using System.Globalization;
using Npgsql;

namespace NetForge.Api;

public sealed record SlaVersionInput(Guid ServiceId,DateOnly EffectiveFrom,
    decimal TargetPercent,decimal CreditCapPercent,string AgreementReference,
    string Exclusions,bool Confirm);

public static class AssuranceAgreements
{
    internal static NpgsqlParameter P(string key,object? value)=>Store.P(key,value);
    internal static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    internal static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher");
    internal static IResult Denied()=>Results.Problem("Staff assurance permissions required",statusCode:403);
    internal static IResult Invalid(string reason)=>Results.BadRequest(new{error=reason});
    internal static IResult Conflict(string reason)=>Results.Conflict(new{error=reason});
    internal static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/assurance/sla-versions",async(HttpContext ctx,NpgsqlDataSource db,
            Guid? serviceId)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT a.id,a.service_id,s.username,c.account,a.version,a.effective_from,
                    a.target_percent,a.credit_cap_percent,a.agreement_reference,
                    a.exclusions,a.created_by,a.created_at
                FROM assurance_sla_versions a
                JOIN services s ON s.id=a.service_id
                JOIN customers c ON c.id=s.customer_id
                WHERE (@service::uuid IS NULL OR a.service_id=@service)
                ORDER BY c.account,a.effective_from DESC,a.version DESC LIMIT 2000
                """,P("service",serviceId)));
        });
        api.MapPost("/assurance/sla-versions",async(HttpContext ctx,
            NpgsqlDataSource db,SlaVersionInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var reference=(input.AgreementReference??"").Trim();
            var exclusions=(input.Exclusions??"").Trim();
            var today=DateOnly.FromDateTime(DateTime.UtcNow);
            if(!input.Confirm || input.ServiceId==Guid.Empty ||
               input.EffectiveFrom<today || input.EffectiveFrom>today.AddYears(1) ||
               input.TargetPercent is <90m or >99.999m ||
               input.CreditCapPercent is <0 or >100 ||
               reference.Length is <5 or >160 || exclusions.Length>2000)
                return Invalid("Confirm current/future terms (within 1 year), target 90–99.999%, cap 0–100%, reference 5–160 characters");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var service=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                "SELECT id,created_at FROM services WHERE id=@id FOR UPDATE",
                P("id",input.ServiceId));
            if(service.Count==0)return Results.NotFound();
            var versions=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT max(version) AS last_version,
                    count(*) FILTER(WHERE effective_from>=@effective) AS later
                FROM assurance_sla_versions WHERE service_id=@service
                """,P("service",input.ServiceId),P("effective",input.EffectiveFrom));
            var record=versions.Single();
            if((long)record["later"]!>0)
                return Conflict("New SLA effective date must be later than all existing versions");
            var last=record["last_version"] is null?0:
                Convert.ToInt32(record["last_version"],CultureInfo.InvariantCulture);
            if(last>=10000)return Conflict("Maximum SLA version count reached");
            var newVersion=last+1;
            var added=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO assurance_sla_versions(service_id,version,effective_from,
                    target_percent,credit_cap_percent,agreement_reference,exclusions,created_by)
                VALUES(@service,@version,@effective,@target,@cap,@reference,@exclusions,@actor)
                RETURNING id
                """,P("service",input.ServiceId),P("version",newVersion),
                P("effective",input.EffectiveFrom),P("target",input.TargetPercent),
                P("cap",input.CreditCapPercent),P("reference",reference),
                P("exclusions",exclusions),P("actor",Actor(ctx)));
            var id=(Guid)added.Single()["id"]!;
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,
                Actor(ctx),"assurance-sla-version","assurance_sla",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/assurance/sla-versions/"+id,
                new{id,version=newVersion,immutable=true,
                    note="Internal SLA term version recorded without changing customer contracts or invoices."});
        });
        AssuranceCredits.Map(api);
    }
}
