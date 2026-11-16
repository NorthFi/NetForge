// root/selfhost/src/NetForge.Api/ServiceLifecycle.cs
// Subscriber access control uses FreeRADIUS Auth-Type := Reject for suspension,
// retaining PPPoE secrets so a deliberate reactivation does not require a reset.
using System.Globalization;
using Npgsql;

namespace NetForge.Api;

public sealed record BillingEnforcementSettings(bool Enabled,int GraceDays,bool RestoreWhenPaid);
public sealed record BillingPolicyResult(bool Enabled,int Suspended,int Restored,string Note);

public static class ServiceLifecycle
{
    private const string HasOverdueInvoice = """
        EXISTS (
          SELECT 1 FROM invoices i
          WHERE i.customer_id=s.customer_id AND i.status='issued'
            AND i.due_date IS NOT NULL AND i.due_date<CURRENT_DATE-@days
            AND i.total>COALESCE(
              (SELECT SUM(p.amount) FROM payments p WHERE p.invoice_id=i.id),0)
        )
        """;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/billing/enforcement",async(HttpContext context,NpgsqlDataSource pool) =>
        {
            if(!context.User.IsInRole("admin"))return Results.Problem("Administrator required",statusCode:403);
            var settings=await Store.Rows(pool,
                "SELECT enabled,grace_days,restore_when_paid,last_run_at FROM billing_controls WHERE id=1");
            var current=settings.Single();
            var preview=await Preview(pool,(int)current["grace_days"]!);
            return Results.Ok(new{settings=current,preview,
                note="Automatic suspension is disabled by default and never sends CoA. Enabling affects new PPPoE authentications only until sessions are explicitly disconnected."});
        });

        api.MapPut("/billing/enforcement",async(HttpContext context,NpgsqlDataSource pool,
            BillingEnforcementSettings input) =>
        {
            if(!context.User.IsInRole("admin"))return Results.Problem("Administrator required",statusCode:403);
            if(input.GraceDays is < 0 or > 120)
                return Results.BadRequest(new{error="Grace period must be 0–120 days"});
            var changed=await Store.Execute(pool,"""
                UPDATE billing_controls SET enabled=@enabled,grace_days=@days,
                    restore_when_paid=@restore,updated_at=now() WHERE id=1
                """,Store.P("enabled",input.Enabled),Store.P("days",input.GraceDays),
                Store.P("restore",input.RestoreWhenPaid));
            if(changed==0)return Results.Problem("Billing control record unavailable",statusCode:503);
            await Store.Audit(pool,context.User.Identity?.Name??"unknown",
                "billing-enforcement-config","billing_controls","1");
            return Results.Ok(new{ok=true,input.Enabled,input.GraceDays,input.RestoreWhenPaid});
        });

        api.MapPost("/billing/enforcement/run",async(HttpContext context,
            NpgsqlDataSource pool,CancellationToken cancellation) =>
        {
            if(!context.User.IsInRole("admin"))return Results.Problem("Administrator required",statusCode:403);
            var result=await Apply(pool,"admin:"+context.User.Identity?.Name,cancellation);
            return Results.Ok(result);
        });
    }

    public static async Task<object> Preview(NpgsqlDataSource pool,int graceDays)
    {
        var days=Math.Clamp(graceDays,0,120);
        var rows=await Store.Rows(pool,$"""
            SELECT
                count(*) FILTER(
                  WHERE s.state='active' AND c.status='active' AND {HasOverdueInvoice}
                ) AS to_suspend,
                count(*) FILTER(
                  WHERE s.state='suspended' AND s.suspension_reason='overdue'
                  AND c.status='active' AND NOT {HasOverdueInvoice}
                  AND EXISTS(SELECT 1 FROM radcheck r
                    WHERE r.username=s.username AND r.attribute='Cleartext-Password')
                ) AS eligible_to_restore
            FROM services s JOIN customers c ON c.id=s.customer_id
            """,Store.P("days",days));
        return rows.Single();
    }

    public static async Task<string> Suspend(NpgsqlDataSource pool,Guid serviceId,string actor)
    {
        await using var conn=await pool.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var service=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
            "SELECT username,state FROM services WHERE id=@id FOR UPDATE",
            Store.P("id",serviceId));
        if(service.Count==0)return "notfound";
        if((string)service[0]["state"]!="cancelled")return "Cancelled services cannot be suspended";
        var username=(string)service[0]["username"]!;
        await Reject(conn,(NpgsqlTransaction)tx,username);
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
            UPDATE services SET state='suspended',suspension_reason='manual',
            suspended_at=now(),changed_at=now() WHERE id=@id
            """,Store.P("id",serviceId));
        await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,actor,"suspend-manual",
            "service",serviceId.ToString());
        await tx.CommitAsync();
        return "ok";
    }

    public static async Task<string> Reactivate(NpgsqlDataSource pool,Guid serviceId,string actor)
    {
        await using var conn=await pool.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            SELECT s.username,s.state,c.status AS customer_status
            FROM services s JOIN customers c ON c.id=s.customer_id
            WHERE s.id=@id FOR UPDATE OF s
            """,Store.P("id",serviceId));
        if(rows.Count==0)return "notfound";
        var s=rows[0];
        if((string)s["customer_status"]!="active")return "Customer must be active";
        if((string)s["state"]!="suspended")return "Only suspended services can be reactivated";
        var username=(string)s["username"]!;
        var credential=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            SELECT id FROM radcheck WHERE username=@u AND attribute='Cleartext-Password'
            LIMIT 1
            """,Store.P("u",username));
        if(credential.Count==0)
            return "No stored PPPoE credential exists; rotate the password before reactivation";
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
            "DELETE FROM radcheck WHERE username=@u AND attribute='Auth-Type' AND value='Reject'",
            Store.P("u",username));
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
            UPDATE services SET state='active',suspension_reason=NULL,
            resumed_at=now(),changed_at=now() WHERE id=@id
            """,Store.P("id",serviceId));
        await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,actor,"reactivate",
            "service",serviceId.ToString());
        await tx.CommitAsync();
        return "ok";
    }

    public static async Task Reject(NpgsqlConnection conn,NpgsqlTransaction tx,string username)
    {
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO radcheck(username,attribute,op,value)
            SELECT @username,'Auth-Type',':=','Reject'
            WHERE NOT EXISTS (
              SELECT 1 FROM radcheck WHERE username=@username
                AND attribute='Auth-Type' AND value='Reject')
            """,Store.P("username",username));
    }

    public static async Task<BillingPolicyResult> Apply(NpgsqlDataSource pool,
        string actor,CancellationToken cancellation)
    {
        await using var conn=await pool.OpenConnectionAsync(cancellation);
        await using var tx=await conn.BeginTransactionAsync(cancellation);
        var locked=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
            "SELECT pg_try_advisory_xact_lock(451201) AS acquired");
        if(locked.Single()["acquired"] is not true)
            return new BillingPolicyResult(false,0,0,"Another policy run is already in progress");

        var settings=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
            "SELECT enabled,grace_days,restore_when_paid FROM billing_controls WHERE id=1 FOR UPDATE");
        var policy=settings.Single();
        if(policy["enabled"] is not true)
            return new BillingPolicyResult(false,0,0,"Disabled; no subscriber changes were applied");
        var days=(int)policy["grace_days"]!;
        var restore=policy["restore_when_paid"] is true;
        var suspended=0;
        var reactivated=0;

        var toSuspend=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,$"""
            SELECT s.id,s.username FROM services s
            JOIN customers c ON c.id=s.customer_id
            WHERE s.state='active' AND c.status='active' AND {HasOverdueInvoice}
            FOR UPDATE OF s
            """,Store.P("days",days));

        foreach(var row in toSuspend)
        {
            cancellation.ThrowIfCancellationRequested();
            var username=(string)row["username"]!;
            var id=(Guid)row["id"]!;
            await Reject(conn,(NpgsqlTransaction)tx,username);
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE services SET state='suspended',suspension_reason='overdue',
                    suspended_at=now(),changed_at=now() WHERE id=@id
                """,Store.P("id",id));
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,actor,
                "auto-suspend-overdue","service",id.ToString());
            suspended++;
        }

        if(restore)
        {
            var toRestore=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,$"""
                SELECT s.id,s.username FROM services s
                JOIN customers c ON c.id=s.customer_id
                WHERE s.state='suspended' AND s.suspension_reason='overdue'
                  AND c.status='active' AND NOT {HasOverdueInvoice}
                  AND EXISTS(SELECT 1 FROM radcheck r
                     WHERE r.username=s.username AND r.attribute='Cleartext-Password')
                FOR UPDATE OF s
                """,Store.P("days",days));

            foreach(var row in toRestore)
            {
                cancellation.ThrowIfCancellationRequested();
                var username=(string)row["username"]!;
                var id=(Guid)row["id"]!;
                await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
                    "DELETE FROM radcheck WHERE username=@u AND attribute='Auth-Type' AND value='Reject'",
                    Store.P("u",username));
                await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                    UPDATE services SET state='active',suspension_reason=NULL,
                        resumed_at=now(),changed_at=now() WHERE id=@id
                    """,Store.P("id",id));
                await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,actor,
                    "auto-restore-paid","service",id.ToString());
                reactivated++;
            }
        }

        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
            "UPDATE billing_controls SET last_run_at=now() WHERE id=1");
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
            INSERT INTO billing_policy_runs(suspended,restored) VALUES (@s,@r)
            """,Store.P("s",suspended),Store.P("r",reactivated));
        await tx.CommitAsync(cancellation);
        return new BillingPolicyResult(true,suspended,reactivated,
            "Policy applied; new authentication is blocked for suspended services. Active NAS sessions were not disconnected.");
    }
}

public sealed class BillingEnforcementWorker(
    NpgsqlDataSource database,ILogger<BillingEnforcementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Opt-in billing policy is disabled by default. Execute at startup then
        // every 10 minutes, with an advisory lock to support multiple API replicas.
        using var periodic=new PeriodicTimer(TimeSpan.FromMinutes(10));
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result=await ServiceLifecycle.Apply(database,"billing-worker",stoppingToken);
                if(result.Suspended>0||result.Restored>0)
                    logger.LogWarning("Billing enforcement applied: {Suspended} suspended, {Restored} restored",
                        result.Suspended,result.Restored);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"Billing enforcement encountered an error");}
            try {if(!await periodic.WaitForNextTickAsync(stoppingToken))break;}
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
        }
    }
}
