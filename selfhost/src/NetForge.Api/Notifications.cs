// root/selfhost/src/NetForge.Api/Notifications.cs
// Durable at-least-once notification outbox; secrets exist only in host environment.
// SMTP supports STARTTLS (e.g. port 587), not implicit TLS/465 or plaintext delivery.
using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace NetForge.Api;

public sealed record NotificationSettingsInput(
    bool CustomerInvoiceEmail, bool OpsEmail, bool OpsTelegram);
public sealed record CustomerEmailOptIn(bool Enabled);

public static class Notifications
{
    private static bool Admin(HttpContext ctx) => ctx.User.IsInRole("admin");
    private static IResult Denied() => Results.Problem("Administrator permissions required",statusCode:403);
    private static string Actor(HttpContext ctx) => ctx.User.Identity?.Name ?? "unknown";
    private static NpgsqlParameter P(string key,object? value) => Store.P(key,value);

    internal static bool EmailReady(IConfiguration config)
    {
        var portText=config["NETFORGE_SMTP_PORT"];
        return !string.IsNullOrWhiteSpace(config["NETFORGE_SMTP_HOST"]) &&
            int.TryParse(portText,out var port) && port is >= 1 and <= 65535 &&
            MailAddress.TryCreate(config["NETFORGE_SMTP_FROM"],out _);
    }
    internal static bool TelegramReady(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config["NETFORGE_TELEGRAM_BOT_TOKEN"]) &&
        !string.IsNullOrWhiteSpace(config["NETFORGE_OPERATIONS_TELEGRAM_CHAT_ID"]);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/notifications/settings",async (HttpContext ctx,
            NpgsqlDataSource db,IConfiguration config)=>
        {
            if(!Admin(ctx))return Denied();
            var rows=await Store.Rows(db,"""
                SELECT customer_invoice_email,ops_email,ops_telegram,updated_at
                FROM notification_settings WHERE id=1
                """);
            return Results.Ok(new {settings=rows.Single(),
                smtpReady=EmailReady(config),
                operationsEmailReady=EmailReady(config) &&
                    MailAddress.TryCreate(config["NETFORGE_OPERATIONS_EMAIL"],out _),
                telegramReady=TelegramReady(config),
                note="Provider credentials are host environment variables and are never returned by this API."});
        });
        api.MapPut("/notifications/settings",async (HttpContext ctx,
            NpgsqlDataSource db,IConfiguration config,NotificationSettingsInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if((input.CustomerInvoiceEmail||input.OpsEmail) && !EmailReady(config))
                return Results.Conflict(new{error="Configure SMTP host, port and From address in the server environment first"});
            if(input.OpsEmail && !MailAddress.TryCreate(config["NETFORGE_OPERATIONS_EMAIL"],out _))
                return Results.Conflict(new{error="Configure a valid NETFORGE_OPERATIONS_EMAIL first"});
            if(input.OpsTelegram && !TelegramReady(config))
                return Results.Conflict(new{error="Configure Telegram bot token and operations chat ID first"});
            await Store.Execute(db,"""
                UPDATE notification_settings
                SET customer_invoice_email=@customer,ops_email=@email,
                    ops_telegram=@telegram,updated_at=now() WHERE id=1
                """,P("customer",input.CustomerInvoiceEmail),P("email",input.OpsEmail),
                P("telegram",input.OpsTelegram));
            await Store.Audit(db,Actor(ctx),"notification-settings","notification_settings","1");
            return Results.Ok(new{ok=true,note="Settings saved. Delivery uses a retrying at-least-once outbox."});
        });
        api.MapPut("/customers/{id:guid}/invoice-notifications",async (
            Guid id,HttpContext ctx,NpgsqlDataSource db,CustomerEmailOptIn body)=>
        {
            if(!Admin(ctx))return Denied();
            var rows=await Store.Rows(db,"SELECT email FROM customers WHERE id=@id",
                P("id",id));
            if(rows.Count==0)return Results.NotFound();
            if(body.Enabled && !MailAddress.TryCreate(rows[0]["email"] as string,out _))
                return Results.Conflict(new{error="Customer must have a valid saved email before opting in"});
            await Store.Execute(db,
                "UPDATE customers SET email_invoice_opt_in=@enabled WHERE id=@id",
                P("enabled",body.Enabled),P("id",id));
            await Store.Audit(db,Actor(ctx),
                body.Enabled?"invoice-email-opt-in":"invoice-email-opt-out","customer",id.ToString());
            return Results.Ok(new{ok=true,enabled=body.Enabled});
        });
        api.MapGet("/notifications/outbox",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT id,channel,recipient,subject,status,attempts,next_attempt_at,
                       claimed_until,last_error,created_at,sent_at
                FROM notification_outbox ORDER BY created_at DESC LIMIT 300
                """));
        });
        api.MapPost("/notifications/outbox/{id:guid}/retry",async(
            Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx))return Denied();
            var count=await Store.Execute(db,"""
                UPDATE notification_outbox SET status='pending',attempts=0,
                    next_attempt_at=now(),claimed_until=NULL,last_error=''
                WHERE id=@id AND status='dead'
                """,P("id",id));
            if(count==0)return Results.Conflict(new{error="Only exhausted notifications can be retried manually"});
            await Store.Audit(db,Actor(ctx),"notification-retry","notification",id.ToString());
            return Results.Ok(new{ok=true});
        });
    }

    // Enqueue in the SAME transaction as invoice issuance.
    internal static Task EnqueueInvoice(NpgsqlConnection conn,NpgsqlTransaction tx,Guid invoiceId)
        => NetworkManagement.Execute(conn,tx,"""
            INSERT INTO notification_outbox(dedupe_key,channel,recipient,subject,body)
            SELECT 'invoice:'||i.id::text,'email',c.email,
                'NetForge invoice '||i.number,
                'Invoice '||i.number||' for account '||c.account||
                '. Amount: ZAR '||i.total::text||
                '. Due: '||COALESCE(to_char(i.due_date,'YYYY-MM-DD'),'not specified')||
                '. Sign in to your ISP customer portal for account details.'
            FROM invoices i JOIN customers c ON c.id=i.customer_id
            JOIN notification_settings n ON n.id=1
            WHERE i.id=@invoice AND i.status='issued'
              AND n.customer_invoice_email=true AND c.email_invoice_opt_in=true
              AND length(trim(c.email))>3
            ON CONFLICT(dedupe_key) DO NOTHING
            """,P("invoice",invoiceId));

    // Operational alerts use the ADMINISTRATIVELY CONFIGURED recipients only.
    internal static async Task EnqueueMonitorEvent(
        NpgsqlConnection conn,NpgsqlTransaction tx,Guid eventId,
        string message,IConfiguration config)
    {
        var settings=await NetworkManagement.Query(conn,tx,"""
            SELECT ops_email,ops_telegram FROM notification_settings WHERE id=1
            """);
        var value=settings.Single();
        var subject="NetForge network alert";
        if(value["ops_email"] is true && EmailReady(config) &&
            MailAddress.TryCreate(config["NETFORGE_OPERATIONS_EMAIL"],out _))
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO notification_outbox(dedupe_key,channel,recipient,subject,body)
                VALUES (@key,'email',@recipient,@subject,@body)
                ON CONFLICT(dedupe_key) DO NOTHING
                """,P("key","monitor:email:"+eventId),
                P("recipient",config["NETFORGE_OPERATIONS_EMAIL"]),
                P("subject",subject),P("body",message));
        if(value["ops_telegram"] is true && TelegramReady(config))
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO notification_outbox(dedupe_key,channel,recipient,subject,body)
                VALUES (@key,'telegram',@recipient,@subject,@body)
                ON CONFLICT(dedupe_key) DO NOTHING
                """,P("key","monitor:telegram:"+eventId),
                P("recipient",config["NETFORGE_OPERATIONS_TELEGRAM_CHAT_ID"]),
                P("subject",subject),P("body",message));
    }

    private static async Task SendEmail(string recipient,string subject,string body,
        IConfiguration config,CancellationToken cancellation)
    {
        var host=config["NETFORGE_SMTP_HOST"];
        if(!EmailReady(config) || !MailAddress.TryCreate(recipient,out var destination))
            throw new InvalidOperationException("SMTP transport or recipient is unavailable");
        var port=int.Parse(config["NETFORGE_SMTP_PORT"]!,
            System.Globalization.CultureInfo.InvariantCulture);
        using var client=new SmtpClient(host!,port)
        {
            DeliveryMethod=SmtpDeliveryMethod.Network,
            EnableSsl=true,  // Requires STARTTLS; never send on plaintext SMTP.
            Timeout=15000
        };
        var user=config["NETFORGE_SMTP_USER"];
        var password=config["NETFORGE_SMTP_PASSWORD"];
        if(!string.IsNullOrEmpty(user))
        {
            if(string.IsNullOrEmpty(password))
                throw new InvalidOperationException("SMTP password is not configured");
            client.Credentials=new NetworkCredential(user,password);
        }
        using var message=new MailMessage(new MailAddress(config["NETFORGE_SMTP_FROM"]!),
            destination){Subject=subject,Body=body,IsBodyHtml=false};
        await client.SendMailAsync(message,cancellation);
    }

    private static async Task SendTelegram(string chat,string body,
        IConfiguration config,CancellationToken cancellation)
    {
        if(!TelegramReady(config))
            throw new InvalidOperationException("Telegram transport is not configured");
        var token=config["NETFORGE_TELEGRAM_BOT_TOKEN"]!;
        using var client=new HttpClient { Timeout=TimeSpan.FromSeconds(12) };
        // HTTP client is not factory-logged: the bot token is part of the Telegram URL.
        var url="https://api.telegram.org/bot"+Uri.EscapeDataString(token)+"/sendMessage";
        using var response=await client.PostAsJsonAsync(url,new {
            chat_id=chat,text=body,disable_web_page_preview=true
        },cancellation);
        if(!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Telegram API rejected message (HTTP "+
                (int)response.StatusCode+")");
        using var stream=await response.Content.ReadAsStreamAsync(cancellation);
        using var json=await JsonDocument.ParseAsync(stream,cancellationToken:cancellation);
        if(!json.RootElement.TryGetProperty("ok",out var ok)||!ok.GetBoolean())
            throw new InvalidOperationException("Telegram API returned an unsuccessful response");
    }

    // Claim and send outside the transaction; callers must tolerate at-least-once
    // delivery after process crashes between provider ACK and SQL marking sent.
    public static async Task<int> ProcessPending(NpgsqlDataSource db,
        IConfiguration config,ILogger logger,CancellationToken cancellation,int limit=10)
    {
        var completed=0;
        for(var index=0;index<limit;index++)
        {
            await using var conn=await db.OpenConnectionAsync(cancellation);
            await using var tx=await conn.BeginTransactionAsync(cancellation);
            var jobs=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT id,channel,recipient,subject,body,attempts
                FROM notification_outbox
                WHERE (status='pending' AND next_attempt_at<=now())
                   OR (status='processing' AND claimed_until<now())
                ORDER BY next_attempt_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
                """);
            if(jobs.Count==0)
            {
                await tx.CommitAsync(cancellation);
                break;
            }
            var job=jobs.Single();
            var id=(Guid)job["id"]!;
            var tries=(short)job["attempts"]!+1;
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                UPDATE notification_outbox SET status='processing',attempts=@tries,
                claimed_until=now()+interval '60 seconds' WHERE id=@id
                """,Store.P("id",id),Store.P("tries",tries));
            await tx.CommitAsync(cancellation);

            string? failure=null;
            try
            {
                var channel=(string)job["channel"]!;
                if(channel=="email")
                    await SendEmail((string)job["recipient"]!,(string)job["subject"]!,
                        (string)job["body"]!,config,cancellation);
                else if(channel=="telegram")
                    await SendTelegram((string)job["recipient"]!,(string)job["body"]!,
                        config,cancellation);
                else throw new InvalidOperationException("Unsupported message transport");
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
            catch(Exception e)
            {
                // Never store provider exception strings that might contain passwords or
                // a Telegram bot token. The classification is sufficient for the UI.
                failure=e is SmtpException ? "SMTP delivery failed" :
                    e is HttpRequestException ? "HTTP delivery failed" :
                    e is TaskCanceledException ? "Delivery timeout" :
                    "Provider rejected or unavailable";
                logger.LogWarning("Notification {NotificationId} delivery failed ({Category})",
                    id,failure);
            }
            if(failure is null)
            {
                await Store.Execute(db,"""
                    UPDATE notification_outbox SET status='sent',sent_at=now(),
                        claimed_until=NULL,last_error='' WHERE id=@id
                    """,Store.P("id",id));
                completed++;
            }
            else
            {
                var delay=Math.Min(3600,30*(1<<Math.Min(tries,6)));
                await Store.Execute(db,"""
                    UPDATE notification_outbox
                    SET status=CASE WHEN attempts>=6 THEN 'dead' ELSE 'pending' END,
                        next_attempt_at=now()+make_interval(secs=>@delay),
                        claimed_until=NULL,last_error=@error
                    WHERE id=@id
                    """,Store.P("id",id),Store.P("delay",delay),Store.P("error",failure));
            }
        }
        return completed;
    }
}

public sealed class NotificationWorker(NpgsqlDataSource database,
    IConfiguration configuration,ILogger<NotificationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        while(!token.IsCancellationRequested)
        {
            try {await Notifications.ProcessPending(database,configuration,logger,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"Notification outbox worker failed");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
}
