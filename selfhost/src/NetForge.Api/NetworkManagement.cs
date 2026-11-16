// root/selfhost/src/NetForge.Api/NetworkManagement.cs
// Manual subscriber lifecycle and scoped RADIUS Dynamic Authorization.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Npgsql;

namespace NetForge.Api;

public sealed record DisconnectInput(Guid RequestId, string Reason, bool Confirm);
public sealed record NasManagementInput(string Vendor, int DisconnectPort, bool DisconnectEnabled);
public sealed record NasSecretInput(string Secret, bool ConfirmRestartRequired);
public sealed record ServicePackageChange(Guid PackageId);

public static class NetworkManagement
{
    private static bool Admin(HttpContext context) => context.User.IsInRole("admin");
    private static IResult Denied() => Results.Problem("Administrator permissions required",statusCode:403);
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? "unknown";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/radius/nas-management",async (HttpContext context,NpgsqlDataSource pool) =>
        {
            if(!Admin(context) && !context.User.IsInRole("dispatcher")) return Denied();
            return Results.Ok(await Store.Rows(pool,"""
                SELECT n.id,n.nasname,n.shortname,n.description,
                       COALESCE(m.vendor,'mikrotik') AS vendor,
                       COALESCE(m.disconnect_port,1700) AS disconnect_port,
                       COALESCE(m.disconnect_enabled,false) AS disconnect_enabled,
                       m.updated_at
                FROM nas n LEFT JOIN nas_management m ON m.nas_id=n.id
                ORDER BY n.shortname
                """));
        });

        api.MapPut("/radius/nas/{id:int}/settings",async (int id,HttpContext context,
            NpgsqlDataSource pool,NasManagementInput input) =>
        {
            if(!Admin(context))return Denied();
            if(input.Vendor is not ("mikrotik" or "generic") ||
                input.DisconnectPort is < 1 or > 65535)
                return Results.BadRequest(new{error="Vendor or Disconnect port invalid"});
            var updated=await Store.Rows(pool,"""
                INSERT INTO nas_management(nas_id,vendor,disconnect_port,disconnect_enabled)
                SELECT id,@vendor,@port,@enabled FROM nas WHERE id=@id
                ON CONFLICT(nas_id) DO UPDATE
                  SET vendor=EXCLUDED.vendor,disconnect_port=EXCLUDED.disconnect_port,
                      disconnect_enabled=EXCLUDED.disconnect_enabled,updated_at=now()
                RETURNING nas_id
                """,Store.P("id",id),Store.P("vendor",input.Vendor),
                Store.P("port",input.DisconnectPort),Store.P("enabled",input.DisconnectEnabled));
            if(updated.Count==0)return Results.NotFound();
            await Store.Audit(pool,Actor(context),"nas-disconnect-settings","nas",id.ToString(CultureInfo.InvariantCulture));
            return Results.Ok(new{ok=true,disconnectEnabled=input.DisconnectEnabled,
                note="Disconnect is restricted to this saved NAS. Firewall and RouterOS /radius incoming configuration are required."});
        });

        api.MapPost("/radius/nas/{id:int}/secret",async (int id,HttpContext context,
            NpgsqlDataSource pool,NasSecretInput input) =>
        {
            if(!Admin(context))return Denied();
            if(!input.ConfirmRestartRequired||string.IsNullOrWhiteSpace(input.Secret)||input.Secret.Length<16||input.Secret.Length>128)
                return Results.BadRequest(new{error="Explicit restart confirmation and a 16–128 character secret are required"});
            var changed=await Store.Execute(pool,"UPDATE nas SET secret=@secret WHERE id=@id",
                Store.P("secret",input.Secret),Store.P("id",id));
            if(changed==0)return Results.NotFound();
            await Store.Audit(pool,Actor(context),"nas-secret-rotate","nas",id.ToString(CultureInfo.InvariantCulture));
            return Results.Ok(new{ok=true,restartRequired=true,
                note="Update the NAS shared secret and restart the FreeRADIUS container. RADIUS authentication may fail until both agree."});
        });

        api.MapGet("/radius/operations",async (HttpContext context,NpgsqlDataSource pool) =>
        {
            if(!Admin(context)&&!context.User.IsInRole("dispatcher"))return Denied();
            return Results.Ok(await Store.Rows(pool,"""
                SELECT id,request_id,radacctid,nas_id,username,acctsessionid,
                       operation,result,actor,reason,response_note,created_at,finished_at
                FROM radius_operations ORDER BY created_at DESC LIMIT 250
                """));
        });

        api.MapPost("/radius/sessions/{id:long}/disconnect",async (
            long id,HttpContext context,NpgsqlDataSource pool,DisconnectInput input,
            CancellationToken cancellation) =>
        {
            if(!Admin(context))return Denied();
            if(!input.Confirm||input.RequestId==Guid.Empty||input.Reason is null||
                input.Reason.Trim().Length is < 5 or > 240)
                return Results.BadRequest(new{error="Confirm the target session, unique request ID and a reason (5–240 characters)"});

            // Idempotency prevents duplicate disconnects if an HTTP client retries.
            var existing=await Store.Rows(pool,
                "SELECT id,result,response_note FROM radius_operations WHERE request_id=@id",
                Store.P("id",input.RequestId));
            if(existing.Count!=0)return Results.Ok(new{operationId=existing[0]["id"],
                result=existing[0]["result"],note=existing[0]["response_note"],replayed=true});

            // Never accept a destination IP, secret, username or session ID from
            // the browser. All targets are resolved from recorded accounting and
            // an administrator-approved SQL NAS entry.
            var rows=await Store.Rows(pool,"""
                SELECT a.radacctid,a.username,a.acctsessionid,
                       host(a.nasipaddress) AS nas_ip,a.acctupdatetime,
                       n.id AS nas_id,n.secret,n.nasname,
                       COALESCE(m.disconnect_port,1700) AS disconnect_port,
                       COALESCE(m.disconnect_enabled,false) AS disconnect_enabled
                FROM radacct a
                JOIN nas n ON n.nasname=host(a.nasipaddress)
                LEFT JOIN nas_management m ON m.nas_id=n.id
                JOIN services s ON s.username=a.username
                WHERE a.radacctid=@id AND a.acctstoptime IS NULL
                """,Store.P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var session=rows[0];
            if(session["disconnect_enabled"] is not true)
                return Results.Conflict(new{error="Disconnect has not been enabled for the matching NAS"});
            if(session["acctupdatetime"] is not DateTime lastUpdate ||
               lastUpdate < DateTime.UtcNow.AddMinutes(-30))
                return Results.Conflict(new{error="Session is stale; confirm its current state at the NAS before disconnecting"});
            var user=session["username"] as string;
            var acctSession=session["acctsessionid"] as string;
            var nasIp=session["nas_ip"] as string;
            var secret=session["secret"] as string;
            if(string.IsNullOrEmpty(user)||string.IsNullOrEmpty(acctSession)||
               string.IsNullOrEmpty(nasIp)||string.IsNullOrEmpty(secret)||
               !IPAddress.TryParse(nasIp,out var ip)||
               ip.AddressFamily!=AddressFamily.InterNetwork)
                return Results.Conflict(new{error="Session has invalid NAS identity or accounting data"});

            var recent=await Store.Rows(pool,"""
                SELECT id,result FROM radius_operations
                WHERE radacctid=@id AND created_at>now()-interval '10 seconds'
                ORDER BY created_at DESC LIMIT 1
                """,Store.P("id",id));
            if(recent.Count!=0)return Results.Conflict(new{error="A disconnect was requested recently; refresh accounting first"});

            var created=await Store.Rows(pool,"""
                INSERT INTO radius_operations(request_id,radacctid,nas_id,username,
                    acctsessionid,operation,actor,reason)
                VALUES (@request,@acct,@nas,@user,@session,'disconnect',@actor,@reason)
                ON CONFLICT(request_id) DO NOTHING RETURNING id
                """,Store.P("request",input.RequestId),Store.P("acct",id),
                Store.P("nas",(int)session["nas_id"]!),Store.P("user",user),
                Store.P("session",acctSession),Store.P("actor",Actor(context)),
                Store.P("reason",input.Reason.Trim()));
            if(created.Count==0)return Results.Conflict(new{error="Duplicate request ID; refresh operation history"});
            var operationId=(Guid)created[0]["id"]!;
            await Store.Audit(pool,Actor(context),"radius-disconnect-request","radacct",id.ToString(CultureInfo.InvariantCulture));

            DynamicAuthResult result;
            try
            {
                result=await RadiusDynamicAuth.DisconnectAsync(
                    nasIp,(int)session["disconnect_port"]!,secret,user,acctSession,cancellation);
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
            {
                result=new DynamicAuthResult("error","HTTP caller disconnected; NAS result unknown");
            }
            catch(Exception)
            {
                result=new DynamicAuthResult("error","Disconnect transport failed; see server logs");
            }
            await Store.Execute(pool,"""
                UPDATE radius_operations SET result=@result,response_note=@note,finished_at=now()
                WHERE id=@id
                """,Store.P("result",result.Status),Store.P("note",result.Detail),
                Store.P("id",operationId));
            await Store.Audit(pool,Actor(context),"radius-disconnect-"+result.Status,
                "radacct",id.ToString(CultureInfo.InvariantCulture));
            return Results.Ok(new{operationId,result=result.Status,note=result.Detail,
                important="Only an accounting Stop confirms the subscriber session has ended."});
        });

        api.MapPut("/services/{id:guid}/package",async(Guid id,HttpContext context,
            NpgsqlDataSource pool,ServicePackageChange input)=>
        {
            if(!Admin(context))return Denied();
            await using var conn=await pool.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var package=await Query(conn,(NpgsqlTransaction)tx,"""
                SELECT id,download_mbps,upload_mbps,mikrotik_rate_limit
                FROM packages WHERE id=@id
                """,Store.P("id",input.PackageId));
            if(package.Count==0)return Results.BadRequest(new{error="Package not found"});
            var rows=await Query(conn,(NpgsqlTransaction)tx,
                "SELECT username,state FROM services WHERE id=@id FOR UPDATE",
                Store.P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var username=(string)rows[0]["username"]!;
            var pkg=package[0];
            var rate=NetworkRate.Effective(
                (int)pkg["upload_mbps"]!, (int)pkg["download_mbps"]!,
                (string)pkg["mikrotik_rate_limit"]!);
            if(!NetworkRate.Valid(rate))
                return Results.BadRequest(new{error="Invalid rate limit configured for selected package"});
            await Execute(conn,(NpgsqlTransaction)tx,
                "UPDATE services SET package_id=@package,changed_at=now() WHERE id=@id",
                Store.P("package",input.PackageId),Store.P("id",id));
            await Execute(conn,(NpgsqlTransaction)tx,
                "DELETE FROM radreply WHERE username=@u AND attribute='Mikrotik-Rate-Limit'",
                Store.P("u",username));
            await Execute(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO radreply(username,attribute,op,value)
                VALUES (@u,'Mikrotik-Rate-Limit','=',@rate)
                """,Store.P("u",username),Store.P("rate",rate));
            await Audit(conn,(NpgsqlTransaction)tx,Actor(context),"service-package-change",
                "service",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,rateLimit=rate,
                note="New sessions receive updated limits; to apply immediately, explicitly disconnect the current session."});
        });

        api.MapPost("/services/{id:guid}/reactivate",async(Guid id,HttpContext context,
            NpgsqlDataSource pool)=>
        {
            if(!Admin(context))return Denied();
            var result=await ServiceLifecycle.Reactivate(pool,id,Actor(context));
            return result switch {
                "ok" => Results.Ok(new{ok=true,note="PPPoE credentials reused for new authentication; existing NAS sessions unchanged."}),
                "notfound" => Results.NotFound(),
                _ => Results.Conflict(new{error=result})
            };
        });

        api.MapGet("/radius/usage",async (HttpContext context,NpgsqlDataSource pool,string? period)=>
        {
            if(!context.User.IsInRole("admin")&&!context.User.IsInRole("dispatcher"))
                return Denied();
            if(!DateOnly.TryParseExact(period,"yyyy-MM",CultureInfo.InvariantCulture,
                  DateTimeStyles.None,out var parsed))
                return Results.BadRequest(new{error="Use ?period=YYYY-MM"});
            var start=new DateOnly(parsed.Year,parsed.Month,1);
            var end=start.AddMonths(1);
            var rows=await Store.Rows(pool,"""
                SELECT s.id AS service_id,s.username,c.account,
                       count(a.radacctid) AS sessions,
                       COALESCE(sum(GREATEST(a.acctinputoctets,0)),0) AS upload_bytes,
                       COALESCE(sum(GREATEST(a.acctoutputoctets,0)),0) AS download_bytes,
                       count(a.radacctid) FILTER(WHERE a.acctstoptime IS NULL) AS unclosed
                FROM services s JOIN customers c ON c.id=s.customer_id
                LEFT JOIN radacct a ON a.username=s.username
                   AND a.acctstarttime>=@start AND a.acctstarttime<@end
                GROUP BY s.id,s.username,c.account ORDER BY c.account LIMIT 10000
                """,Store.P("start",start.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),
                Store.P("end",end.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)));
            return Results.Ok(new{period=start.ToString("yyyy-MM",CultureInfo.InvariantCulture),
                records=rows,
                note="Accounting counters are cumulative per session and grouped by session start month. Long-running sessions crossing month boundaries are NOT split and figures are not billing-grade."});
        });
    }

    internal static async Task<List<Dictionary<string,object?>>> Query(NpgsqlConnection connection,
        NpgsqlTransaction transaction,string sql,params NpgsqlParameter[] args)
    {
        await using var command=new NpgsqlCommand(sql,connection,transaction);
        command.Parameters.AddRange(args);
        return await Store.Read(command);
    }

    internal static async Task Execute(NpgsqlConnection connection,NpgsqlTransaction transaction,
        string sql,params NpgsqlParameter[] args)
    {
        await using var command=new NpgsqlCommand(sql,connection,transaction);
        command.Parameters.AddRange(args);
        await command.ExecuteNonQueryAsync();
    }

    internal static Task Audit(NpgsqlConnection connection,NpgsqlTransaction transaction,
        string actor,string action,string entity,string id) =>
        Execute(connection,transaction,"""
            INSERT INTO audit_log(username,action,entity,entity_id)
            VALUES (@actor,@action,@entity,@id)
            """,Store.P("actor",actor),Store.P("action",action),
            Store.P("entity",entity),Store.P("id",id));
}

public static class NetworkRate
{
    // MikroTik-Rate-Limit is router receive/transmit (subscriber upload/download).
    public static string Effective(int uploadMbps,int downloadMbps,string? overrideValue) =>
        string.IsNullOrWhiteSpace(overrideValue)
            ? $"{uploadMbps}M/{downloadMbps}M" : overrideValue.Trim();

    // Supports a simple deterministic two-value rate. Complex RouterOS burst
    // syntax is intentionally excluded until a structured rate plan editor exists.
    public static bool Valid(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            value,@"^[1-9][0-9]{0,6}[kMG]/[1-9][0-9]{0,6}[kMG]$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
