// root/selfhost/src/NetForge.Api/BillingScheduler.cs
// UTC monthly recurring invoices. No proration, VAT, contract or payment gateway.
// Automatic draft creation/issuance is OFF until an administrator enables it.
using System.Globalization;
using Npgsql;

namespace NetForge.Api;

public sealed record BillingScheduleInput(
    bool Enabled,bool AutoIssue,int RunDay,int DueDays);
public sealed record BillingScheduleRunInput(DateOnly Period,bool Confirm);

public sealed record BillingScheduleResult(
    bool Enabled,int Created,int Issued,string Note);

public static class BillingScheduler
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static IResult Denied()=>Results.Problem("Administrator privileges required",statusCode:403);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/billing/schedule",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Denied();
            var rows=await Store.Rows(db,"""
                SELECT enabled,auto_issue,run_day,due_days,last_run_at,updated_at
                FROM billing_schedule WHERE id=1
                """);
            return Results.Ok(new{settings=rows.Single(),
                note="Disabled by default. Calendar is UTC. Monthly invoice values are snapshots of currently active subscriptions."});
        });
        api.MapPut("/billing/schedule",async(HttpContext ctx,
            NpgsqlDataSource db,BillingScheduleInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(input.RunDay is < 1 or > 28 || input.DueDays is < 1 or > 90)
                return Results.BadRequest(new{error="Run day 1–28 and due days 1–90 required"});
            if(input.AutoIssue && !input.Enabled)
                return Results.BadRequest(new{error="Automatic issuance requires billing schedule enabled"});
            await Store.Execute(db,"""
                UPDATE billing_schedule SET enabled=@enabled,auto_issue=@issue,
                run_day=@day,due_days=@due,updated_at=now() WHERE id=1
                """,P("enabled",input.Enabled),P("issue",input.AutoIssue),
                P("day",input.RunDay),P("due",input.DueDays));
            await Store.Audit(db,ctx.User.Identity?.Name??"unknown","billing-schedule-config",
                "billing_schedule","1");
            return Results.Ok(new{ok=true,note="Changes recorded; enabling can create real customer invoices on the next scheduled run."});
        });
        api.MapGet("/billing/schedule/preview",async(HttpContext ctx,
            NpgsqlDataSource db,string? period)=>
        {
            if(!Admin(ctx))return Denied();
            if(!ValidPeriod(period,out var month))
                return Results.BadRequest(new{error="Use period=YYYY-MM"});
            var rows=await Store.Rows(db,"""
                SELECT count(*) AS customers,COALESCE(sum(subtotal),0) AS total
                FROM (
                  SELECT s.customer_id,sum(p.monthly_price) AS subtotal
                  FROM services s JOIN customers c ON c.id=s.customer_id
                  JOIN packages p ON p.id=s.package_id
                  WHERE s.state='active' AND c.status='active' AND p.monthly_price>0
                    AND NOT EXISTS (
                      SELECT 1 FROM invoices i
                      WHERE i.customer_id=s.customer_id AND i.period=@period
                    )
                  GROUP BY s.customer_id
                ) eligible
                """,P("period",month));
            return Results.Ok(new{
                period=month.ToString("yyyy-MM",CultureInfo.InvariantCulture),
                eligible=rows.Single(),note="Read-only preview. No invoice created or email sent."});
        });
        api.MapPost("/billing/schedule/run",async(HttpContext ctx,
            NpgsqlDataSource db,BillingScheduleRunInput input,CancellationToken cancellation)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm||input.Period.Day!=1)
                return Results.BadRequest(new{error="Confirm an exact first-of-month period"});
            var result=await Run(db,input.Period,"admin:"+(ctx.User.Identity?.Name??"unknown"),
                cancellation);
            return Results.Ok(result);
        });
        api.MapGet("/billing/schedule/runs",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT period,created_count,issued_count,actor,occurred_at
                FROM billing_schedule_runs ORDER BY id DESC LIMIT 100
                """));
        });
        api.MapGet("/billing/invoices/{id:guid}/items",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db)=>
        {
            if(!Admin(ctx)&&!ctx.User.IsInRole("dispatcher"))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT description,quantity,unit_price,amount
                FROM invoice_items WHERE invoice_id=@id ORDER BY id
                """,P("id",id)));
        });
    }

    private static bool ValidPeriod(string? value,out DateOnly period)
    {
        period=default;
        return !string.IsNullOrEmpty(value) &&
            DateOnly.TryParseExact(value,"yyyy-MM",CultureInfo.InvariantCulture,
                DateTimeStyles.None,out period);
    }

    public static async Task<BillingScheduleResult> Run(NpgsqlDataSource db,
        DateOnly period,string actor,CancellationToken cancellation)
    {
        if(period.Day!=1)throw new ArgumentException("Period must begin on first day of month");
        await using var conn=await db.OpenConnectionAsync(cancellation);
        await using var tx=await conn.BeginTransactionAsync(cancellation);
        var lockRows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
            "SELECT pg_try_advisory_xact_lock(572211) AS locked");
        if(lockRows.Single()["locked"] is not true)
            return new BillingScheduleResult(false,0,0,"Another billing run is already active");

        var settings=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            SELECT enabled,auto_issue,due_days FROM billing_schedule WHERE id=1 FOR UPDATE
            """);
        var config=settings.Single();
        if(config["enabled"] is not true)
            return new BillingScheduleResult(false,0,0,
                "Automated billing is disabled; no records changed");
        var issue=config["auto_issue"] is true;
        var due=(int)config["due_days"]!;

        // All invoice and item calculations use one MATERIALIZED subscription
        // snapshot, preventing a package change between the invoice subtotal and
        // its itemization within this SQL statement.
        var created=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            WITH eligible AS MATERIALIZED (
                SELECT s.id AS service_id,s.customer_id,p.name AS package,
                       p.monthly_price
                FROM services s JOIN customers c ON c.id=s.customer_id
                JOIN packages p ON p.id=s.package_id
                WHERE s.state='active' AND c.status='active' AND p.monthly_price>0
            ), totals AS (
                SELECT customer_id,sum(monthly_price) AS total
                FROM eligible GROUP BY customer_id
            ), created AS (
                INSERT INTO invoices(customer_id,number,period,total,due_date)
                SELECT t.customer_id,
                    'NF-'||to_char(@period::date,'YYYYMM')||'-'||t.customer_id::text,
                    @period::date,t.total,(@period::date+@due)::date
                FROM totals t
                ON CONFLICT(customer_id,period) DO NOTHING
                RETURNING id,customer_id,number,total
            ), items AS (
                INSERT INTO invoice_items(invoice_id,service_id,description,
                    quantity,unit_price,amount)
                SELECT i.id,e.service_id,'Subscription: '||e.package,1,
                    e.monthly_price,e.monthly_price
                FROM created i JOIN eligible e ON e.customer_id=i.customer_id
                RETURNING id
            )
            SELECT id,number,total FROM created ORDER BY number
            """,P("period",period),P("due",due));

        var issued=0;
        if(issue)
        {
            foreach(var invoice in created)
            {
                cancellation.ThrowIfCancellationRequested();
                var id=(Guid)invoice["id"]!;
                var amount=(decimal)invoice["total"]!;
                await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                    UPDATE invoices SET status='issued' WHERE id=@id AND status='draft'
                    """,P("id",id));
                await FinancialAccounting.PostJournal(conn,(NpgsqlTransaction)tx,
                    "invoice",id,"Automated monthly invoice "+invoice["number"],actor,
                    "AR","REVENUE",amount);
                await Notifications.EnqueueInvoice(conn,(NpgsqlTransaction)tx,id);
                issued++;
            }
        }
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
            "UPDATE billing_schedule SET last_run_at=now() WHERE id=1");
        if(created.Count>0)
        {
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO billing_schedule_runs(period,created_count,issued_count,actor)
                VALUES(@period,@created,@issued,@actor)
                """,P("period",period),P("created",created.Count),
                P("issued",issued),P("actor",actor));
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,actor,
                "monthly-billing-run","invoice",period.ToString("yyyy-MM",
                    CultureInfo.InvariantCulture));
        }
        await tx.CommitAsync(cancellation);
        return new BillingScheduleResult(true,created.Count,issued,
            "Only previously unbilled active subscriptions were drafted. Existing customer/month invoices were never modified. VAT and proration are not implemented.");
    }
}

public sealed class MonthlyBillingWorker(NpgsqlDataSource db,
    ILogger<MonthlyBillingWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromHours(1));
        while(!token.IsCancellationRequested)
        {
            try
            {
                var now=DateTime.UtcNow;
                var settings=await Store.Rows(db,
                    "SELECT enabled,run_day FROM billing_schedule WHERE id=1");
                if(settings.Single()["enabled"] is true &&
                    now.Day >= (int)settings.Single()["run_day"]!)
                {
                    var month=new DateOnly(now.Year,now.Month,1);
                    var result=await BillingScheduler.Run(db,month,"billing-worker",token);
                    if(result.Created>0)
                        logger.LogInformation("Monthly billing created {Created} and issued {Issued} invoices",
                            result.Created,result.Issued);
                }
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception error){logger.LogError(error,"Monthly billing worker failed");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
}
