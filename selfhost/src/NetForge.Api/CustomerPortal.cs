// root/selfhost/src/NetForge.Api/CustomerPortal.cs
// Subscriber identity and data boundary; never reuse staff credentials or roles.
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace NetForge.Api;

public sealed record PortalEnrollment(Guid CustomerId,string Username,string Password);
public sealed record PortalAccountChange(string Action,string? Password);
public sealed record PortalLogin(string Username,string Password);
public sealed record PortalPasswordChange(string CurrentPassword,string NewPassword);
public sealed record PortalTicketCreate(string Subject,string Description);
public sealed record PortalReplyCreate(string Body);
public sealed record StaffTicketReply(string Body,string Visibility);

public static class CustomerPortal
{
    public const string Scheme = "NetForge.Portal";
    private static readonly Regex UsernamePattern =
        new(@"^[A-Za-z0-9_.@-]{3,80}$",RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void ConfigureCookie(CookieAuthenticationOptions options)
    {
        options.Cookie.Name="NetForge.Portal";
        options.Cookie.Path="/api/portal";
        options.Cookie.HttpOnly=true;
        options.Cookie.SameSite=SameSiteMode.Strict;
        options.Cookie.SecurePolicy=CookieSecurePolicy.Always;
        options.ExpireTimeSpan=TimeSpan.FromHours(6);
        options.SlidingExpiration=true;
        options.Events.OnRedirectToLogin=ctx=>{
            ctx.Response.StatusCode=401;return Task.CompletedTask;};
        options.Events.OnRedirectToAccessDenied=ctx=>{
            ctx.Response.StatusCode=403;return Task.CompletedTask;};
        options.Events.OnValidatePrincipal=ValidatePrincipal;
    }

    private static async Task ValidatePrincipal(CookieValidatePrincipalContext ctx)
    {
        var idText=ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var verText=ctx.Principal?.FindFirstValue("portal_version");
        if(!Guid.TryParse(idText,out var customerId) ||
            !int.TryParse(verText,out var version))
        {
            ctx.RejectPrincipal();
            return;
        }
        // A disabled or reset account revokes existing sessions immediately.
        var pool=ctx.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
        var accounts=await Store.Rows(pool,"""
            SELECT p.password_version,p.enabled FROM customer_portal_accounts p
            JOIN customers c ON c.id=p.customer_id
            WHERE p.customer_id=@id
            """,Store.P("id",customerId));
        if(accounts.Count!=1 || accounts[0]["enabled"] is not true ||
           (int)accounts[0]["password_version"]! != version)
        {
            ctx.RejectPrincipal();
        }
    }

    private static string Actor(HttpContext ctx) => ctx.User.Identity?.Name??"unknown";
    private static IResult AdminDenied() => Results.Problem("Administrator required",statusCode:403);
    private static bool IsAdmin(HttpContext ctx) => ctx.User.IsInRole("admin");
    private static Guid Customer(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static string NonBlank(string? s) => (s??"").Trim();

    public static void MapStaff(RouteGroupBuilder staff)
    {
        staff.MapGet("/customer-portal/accounts",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!IsAdmin(ctx))return AdminDenied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.customer_id,p.username,p.enabled,p.password_version,
                       p.updated_at,c.account
                FROM customer_portal_accounts p
                JOIN customers c ON c.id=p.customer_id ORDER BY c.account
                """));
        });

        staff.MapPost("/customer-portal/accounts",async(HttpContext ctx,NpgsqlDataSource db,
            PortalEnrollment body)=>
        {
            if(!IsAdmin(ctx))return AdminDenied();
            var username=NonBlank(body.Username);
            if(!UsernamePattern.IsMatch(username)||body.CustomerId==Guid.Empty ||
                body.Password is null ||body.Password.Length is < 14 or > 1024)
                return Results.BadRequest(new{error="Valid customer, username and unique password (14–1024 characters) are required"});
            var found=await Store.Rows(db,"SELECT id FROM customers WHERE id=@id",
                Store.P("id",body.CustomerId));
            if(found.Count==0)return Results.NotFound(new{error="Customer not found"});
            await Store.Execute(db,"""
                INSERT INTO customer_portal_accounts(customer_id,username,password_hash)
                VALUES(@id,@username,@hash)
                """,Store.P("id",body.CustomerId),Store.P("username",username),
                Store.P("hash",Passwords.Hash(body.Password)));
            await Store.Audit(db,Actor(ctx),"portal-enroll","customer",body.CustomerId.ToString());
            return Results.Created("/api/customer-portal/accounts",new{
                customerId=body.CustomerId,username,enabled=true,
                note="Share the password privately; NetForge never returns it again."});
        });

        staff.MapPut("/customer-portal/accounts/{id:guid}",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,PortalAccountChange body)=>
        {
            if(!IsAdmin(ctx))return AdminDenied();
            if(body.Action is not ("enable" or "disable" or "reset"))
                return Results.BadRequest(new{error="Action must be enable, disable or reset"});
            if(body.Action=="reset" && (body.Password is null || body.Password.Length is < 14 or > 1024))
                return Results.BadRequest(new{error="New portal password must be 14–1024 characters"});
            // Every administrative change increments the password version to
            // invalidate *all* old cookies, including those held on other devices.
            var updated=await Store.Execute(db,"""
                UPDATE customer_portal_accounts
                SET enabled=CASE WHEN @action='enable' THEN true
                                 WHEN @action='disable' THEN false ELSE enabled END,
                    password_hash=CASE WHEN @action='reset' THEN @hash ELSE password_hash END,
                    password_version=password_version+1,updated_at=now()
                WHERE customer_id=@id
                """,Store.P("id",id),Store.P("action",body.Action),
                Store.P("hash",body.Action=="reset"?Passwords.Hash(body.Password!):null));
            if(updated==0)return Results.NotFound();
            await Store.Audit(db,Actor(ctx),"portal-"+body.Action,"customer",id.ToString());
            return Results.Ok(new{ok=true,revokedExistingSessions=true});
        });

        staff.MapGet("/tickets/{id:guid}/messages",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!StaffRead(ctx))return Results.Problem("Staff permissions required",statusCode:403);
            var exists=await Store.Rows(db,"SELECT id FROM tickets WHERE id=@id",Store.P("id",id));
            if(exists.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT id,visibility,actor_type,author,body,created_at
                FROM ticket_messages WHERE ticket_id=@id ORDER BY created_at,id
                """,Store.P("id",id)));
        });

        staff.MapPost("/tickets/{id:guid}/messages",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,StaffTicketReply input)=>
        {
            if(!StaffWrite(ctx))return Results.Problem("Staff permissions required",statusCode:403);
            var body=NonBlank(input.Body);
            if(body.Length is < 1 or > 4000 || input.Visibility is not ("public" or "internal"))
                return Results.BadRequest(new{error="Message must be 1–4000 characters and public or internal"});
            var created=await Store.Rows(db,"""
                INSERT INTO ticket_messages(ticket_id,visibility,actor_type,author,body)
                SELECT id,@visibility,'staff',@author,@body
                FROM tickets WHERE id=@id RETURNING id
                """,Store.P("id",id),Store.P("visibility",input.Visibility),
                Store.P("author",Actor(ctx)),Store.P("body",body));
            if(created.Count==0)return Results.NotFound();
            await Store.Audit(db,Actor(ctx),"ticket-message-"+input.Visibility,
                "ticket",id.ToString());
            return Results.Created($"/api/tickets/{id}/messages",new{id=created[0]["id"]});
        });
    }

    private static bool StaffRead(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated==true;
    private static bool StaffWrite(HttpContext ctx) => new[]{"admin","dispatcher","technician"}
        .Any(ctx.User.IsInRole);

    public static void MapPortal(WebApplication app)
    {
        var group=app.MapGroup("/api/portal")
            .RequireAuthorization(new AuthorizeAttribute{AuthenticationSchemes=Scheme});
        group.AddEndpointFilter(async (filterContext,next)=>
        {
            var ctx=filterContext.HttpContext;
            if(HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method) ||
                HttpMethods.IsPatch(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method))
            {
                var expected=ctx.User.FindFirstValue("csrf")??"";
                var provided=ctx.Request.Headers["X-CSRF-Token"].ToString();
                if(expected.Length!=64 || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected),Encoding.UTF8.GetBytes(provided)))
                    return Results.Problem("Portal CSRF token required",statusCode:403);
            }
            return await next(filterContext);
        });

        app.MapPost("/api/portal/login",async(HttpContext ctx,NpgsqlDataSource db,
            PortalLogin input)=>
        {
            var name=NonBlank(input.Username);
            if(name.Length is < 3 or > 80||input.Password is null||input.Password.Length>2048)
                return Results.Unauthorized();
            var rows=await Store.Rows(db,"""
                SELECT p.customer_id,p.username,p.password_hash,p.password_version,p.enabled
                FROM customer_portal_accounts p JOIN customers c ON c.id=p.customer_id
                WHERE lower(p.username)=lower(@username)
                """,Store.P("username",name));
            var record=rows.FirstOrDefault();
            if(record is null || record["enabled"] is not true ||
                !Passwords.Verify(input.Password,(string)record["password_hash"]!))
                return Results.Unauthorized();

            var csrf=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var claims=new[]{
                new Claim(ClaimTypes.NameIdentifier,record["customer_id"]!.ToString()!),
                new Claim(ClaimTypes.Name,(string)record["username"]!),
                new Claim("portal_version",record["password_version"]!.ToString()!),
                new Claim("csrf",csrf)
            };
            await ctx.SignInAsync(Scheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme)));
            await Store.Audit(db,(string)record["username"]!,"portal-login","customer",
                record["customer_id"]!.ToString()!);
            return Results.Ok(new{authenticated=true,csrf,
                username=record["username"]});
        }).RequireRateLimiting("login");

        group.MapGet("/session",(ClaimsPrincipal user)=>
            Results.Ok(new{authenticated=true,
                username=user.Identity?.Name,csrf=user.FindFirstValue("csrf")}));
        group.MapPost("/logout",async(HttpContext ctx)=>
        {
            await ctx.SignOutAsync(Scheme);
            return Results.Ok(new{ok=true});
        });

        group.MapGet("/profile",async(ClaimsPrincipal user,NpgsqlDataSource db)=>
        {
            var data=await Store.Rows(db,"""
                SELECT id,account,first_name,surname,email,phone,address,status,created_at
                FROM customers WHERE id=@id
                """,Store.P("id",Customer(user)));
            return data.Count==0?Results.NotFound():Results.Ok(data[0]);
        });

        group.MapGet("/services",async(ClaimsPrincipal user,NpgsqlDataSource db)=>
            Results.Ok(await Store.Rows(db,"""
                SELECT s.id,s.username,s.state,s.created_at,s.changed_at,
                       p.name AS package,p.download_mbps,p.upload_mbps
                FROM services s JOIN packages p ON p.id=s.package_id
                WHERE s.customer_id=@id ORDER BY s.created_at DESC
                """,Store.P("id",Customer(user)))));

        group.MapGet("/invoices",async(ClaimsPrincipal user,NpgsqlDataSource db)=>
            Results.Ok(await Store.Rows(db,"""
                SELECT i.id,i.number,i.period,i.total,i.status,i.due_date,i.created_at,
                       COALESCE(sum(p.amount),0) AS paid,
                       i.total-COALESCE(sum(p.amount),0) AS outstanding
                FROM invoices i LEFT JOIN payments p ON p.invoice_id=i.id
                WHERE i.customer_id=@id AND i.status IN ('issued','paid')
                GROUP BY i.id ORDER BY i.created_at DESC LIMIT 500
                """,Store.P("id",Customer(user)))));

        group.MapGet("/payments",async(ClaimsPrincipal user,NpgsqlDataSource db)=>
            Results.Ok(await Store.Rows(db,"""
                SELECT p.id,p.amount,p.reference,p.received_at,i.number AS invoice
                FROM payments p JOIN invoices i ON i.id=p.invoice_id
                WHERE i.customer_id=@id AND i.status IN ('issued','paid')
                ORDER BY p.received_at DESC LIMIT 500
                """,Store.P("id",Customer(user)))));

        group.MapGet("/tickets",async(ClaimsPrincipal user,NpgsqlDataSource db)=>
            Results.Ok(await Store.Rows(db,"""
                SELECT id,number,subject,description,type,status,priority,created_at
                FROM tickets WHERE customer_id=@id ORDER BY created_at DESC LIMIT 500
                """,Store.P("id",Customer(user)))));

        group.MapPost("/tickets",async(ClaimsPrincipal user,NpgsqlDataSource db,
            PortalTicketCreate input)=>
        {
            var subject=NonBlank(input.Subject);
            var body=NonBlank(input.Description);
            if(subject.Length is < 4 or > 200||body.Length is < 5 or > 3000)
                return Results.BadRequest(new{error="Subject 4–200 and description 5–3000 characters required"});
            var customerId=Customer(user);
            var created=await Store.Rows(db,"""
                INSERT INTO tickets(customer_id,highsite_id,subject,description,type,status,priority)
                SELECT id,highsite_id,@subject,@body,'customer','open','normal'
                FROM customers WHERE id=@id RETURNING id,number
                """,Store.P("id",customerId),Store.P("subject",subject),Store.P("body",body));
            if(created.Count==0)return Results.NotFound();
            await Store.Audit(db,user.Identity?.Name??"customer","portal-ticket-create",
                "ticket",created[0]["id"]!.ToString()!);
            return Results.Created($"/api/portal/tickets/{created[0]["id"]}",
                created[0]);
        });

        group.MapGet("/tickets/{id:guid}/messages",async(Guid id,ClaimsPrincipal user,
            NpgsqlDataSource db)=>
        {
            var tickets=await Store.Rows(db,"""
                SELECT id FROM tickets WHERE id=@ticket AND customer_id=@customer
                """,Store.P("ticket",id),Store.P("customer",Customer(user)));
            if(tickets.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT m.id,m.actor_type,m.author,m.body,m.created_at
                FROM ticket_messages m WHERE m.ticket_id=@ticket AND m.visibility='public'
                ORDER BY m.created_at,m.id
                """,Store.P("ticket",id)));
        });

        group.MapPost("/tickets/{id:guid}/messages",async(Guid id,ClaimsPrincipal user,
            NpgsqlDataSource db,PortalReplyCreate input)=>
        {
            var body=NonBlank(input.Body);
            if(body.Length is < 1 or > 4000)
                return Results.BadRequest(new{error="Message must be 1–4000 characters"});
            var customerId=Customer(user);
            var created=await Store.Rows(db,"""
                INSERT INTO ticket_messages(ticket_id,visibility,actor_type,author,body)
                SELECT t.id,'public','customer',@author,@body
                FROM tickets t WHERE t.id=@ticket AND t.customer_id=@customer
                  AND t.status NOT IN ('closed','resolved')
                RETURNING id
                """,Store.P("ticket",id),Store.P("customer",customerId),
                Store.P("author",user.Identity?.Name??"customer"),Store.P("body",body));
            if(created.Count==0)return Results.Conflict(new{
                error="Ticket not found, does not belong to your account, or is closed"});
            await Store.Audit(db,user.Identity?.Name??"customer","portal-ticket-reply",
                "ticket",id.ToString());
            return Results.Created($"/api/portal/tickets/{id}/messages",
                new{id=created[0]["id"]});
        });

        group.MapPost("/password",async(HttpContext ctx,ClaimsPrincipal user,
            NpgsqlDataSource db,PortalPasswordChange input)=>
        {
            if(input.NewPassword is null||input.NewPassword.Length is < 14 or > 1024)
                return Results.BadRequest(new{error="New password must be 14–1024 characters"});
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await using var cmd=new NpgsqlCommand("""
                SELECT password_hash FROM customer_portal_accounts
                WHERE customer_id=@customer AND enabled=true FOR UPDATE
                """,conn,(NpgsqlTransaction)tx);
            cmd.Parameters.AddWithValue("customer",Customer(user));
            var hash=await cmd.ExecuteScalarAsync() as string;
            if(hash is null||!Passwords.Verify(input.CurrentPassword??"",hash))
                return Results.Problem("Current password incorrect",statusCode:403);
            await using var update=new NpgsqlCommand("""
                UPDATE customer_portal_accounts
                SET password_hash=@hash,password_version=password_version+1,updated_at=now()
                WHERE customer_id=@customer RETURNING password_version
                """,conn,(NpgsqlTransaction)tx);
            update.Parameters.AddWithValue("hash",Passwords.Hash(input.NewPassword));
            update.Parameters.AddWithValue("customer",Customer(user));
            var version=(int)(await update.ExecuteScalarAsync())!;
            await tx.CommitAsync();
            // Same browser remains authenticated with a new version and CSRF;
            // all other cookies with the old version are revoked.
            var csrf=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var claims=new[]{
                new Claim(ClaimTypes.NameIdentifier,Customer(user).ToString()),
                new Claim(ClaimTypes.Name,user.Identity?.Name??"customer"),
                new Claim("portal_version",version.ToString()),
                new Claim("csrf",csrf)
            };
            await ctx.SignInAsync(Scheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme)));
            await Store.Audit(db,user.Identity?.Name??"customer","portal-password-change",
                "customer",Customer(user).ToString());
            return Results.Ok(new{ok=true,csrf});
        });
    }
}
