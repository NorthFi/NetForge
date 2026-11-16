// root/selfhost/src/NetForge.Api/UsageAccounting.cs
// Observed RADIUS counter deltas; month boundaries allocate interval volume
// proportionally to elapsed seconds. This is an estimate, not a billing ledger.
using System.Globalization;
using System.Security.Claims;
using Npgsql;

namespace NetForge.Api;

public static class UsageAccounting
{
    public static void MapStaff(RouteGroupBuilder staff)
    {
        staff.MapGet("/radius/usage-reconciled",async(HttpContext ctx,
            NpgsqlDataSource db,string? period)=>
        {
            if(!ctx.User.IsInRole("admin")&&!ctx.User.IsInRole("dispatcher"))
                return Results.Problem("Staff billing or dispatcher access required",statusCode:403);
            if(!TryPeriod(period,out var start,out var end))
                return Results.BadRequest(new{error="Use YYYY-MM for the reporting month"});
            return Results.Ok(await Query(db,start,end,null));
        });
    }

    public static void MapPortal(RouteGroupBuilder portal)
    {
        portal.MapGet("/usage",async(ClaimsPrincipal user,
            NpgsqlDataSource db,string? period)=>
        {
            if(!TryPeriod(period,out var start,out var end))
                return Results.BadRequest(new{error="Use YYYY-MM for the reporting month"});
            var customer=Guid.Parse(user.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            return Results.Ok(await Query(db,start,end,customer));
        });
    }

    private static bool TryPeriod(string? requested,out DateTime start,out DateTime end)
    {
        var month=DateTime.UtcNow;
        if(!string.IsNullOrWhiteSpace(requested))
        {
            if(!DateOnly.TryParseExact(requested,"yyyy-MM",CultureInfo.InvariantCulture,
                DateTimeStyles.None,out var date))
            {
                start=default;end=default;
                return false;
            }
            month=new DateTime(date.Year,date.Month,1,0,0,0,DateTimeKind.Utc);
        }
        start=new DateTime(month.Year,month.Month,1,0,0,0,DateTimeKind.Utc);
        end=start.AddMonths(1);
        return true;
    }

    private static async Task<object> Query(NpgsqlDataSource db,DateTime start,DateTime end,Guid? customer)
    {
        var records=await Store.Rows(db,"""
            WITH intervals AS (
                SELECT s.id AS service_id,s.username,c.account,
                       u.id AS interval_id,
                       COALESCE(
                         u.input_bytes::numeric *
                         EXTRACT(EPOCH FROM LEAST(u.ended_at,@end)-GREATEST(u.started_at,@start))
                         / NULLIF(EXTRACT(EPOCH FROM u.ended_at-u.started_at),0),0
                       ) AS upload_fraction,
                       COALESCE(
                         u.output_bytes::numeric *
                         EXTRACT(EPOCH FROM LEAST(u.ended_at,@end)-GREATEST(u.started_at,@start))
                         / NULLIF(EXTRACT(EPOCH FROM u.ended_at-u.started_at),0),0
                       ) AS download_fraction
                FROM services s JOIN customers c ON c.id=s.customer_id
                LEFT JOIN radius_usage_intervals u ON u.username=s.username
                     AND u.started_at<@end AND u.ended_at>@start
                WHERE (@customer::uuid IS NULL OR s.customer_id=@customer)
            )
            SELECT service_id,username,account,
                   COALESCE(floor(sum(upload_fraction)),0)::bigint AS upload_bytes,
                   COALESCE(floor(sum(download_fraction)),0)::bigint AS download_bytes,
                   count(interval_id) AS observed_intervals
            FROM intervals
            GROUP BY service_id,username,account ORDER BY account,username
            LIMIT 10000
            """,Store.P("start",start),Store.P("end",end),
            Store.P("customer",customer));
        // Count anomalies for the same scoped services; anomalies are not silently
        // charged. A separate sample-quality indicator lets operators investigate.
        var diagnostics=await Store.Rows(db,"""
            SELECT count(*) AS anomaly_count
            FROM radius_usage_anomalies a
            JOIN services s ON s.username=a.username
            WHERE a.observed_at>=@start AND a.observed_at<@end
              AND (@customer::uuid IS NULL OR s.customer_id=@customer)
            """,Store.P("start",start),Store.P("end",end),
            Store.P("customer",customer));
        return new
        {
            period=start.ToString("yyyy-MM",CultureInfo.InvariantCulture),
            interval="UTC",
            records,
            anomalyCount=diagnostics.Single()["anomaly_count"],
            note="Estimated interval usage split by elapsed seconds at UTC month boundaries. Historical sessions before migration 003 are not reconstructed. NAS counter resets and out-of-order samples are excluded and flagged."
        };
    }
}
