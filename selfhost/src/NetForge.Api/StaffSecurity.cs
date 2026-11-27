// root/selfhost/src/NetForge.Api/StaffSecurity.cs
// Server-authoritative staff sessions, immediate revocation on the next request,
// and audited administrative lifecycle controls. Portal cookies are independent.
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

namespace NetForge.Api;

public sealed record StaffAccessChange(string Role,bool Enabled,bool Confirm);
public sealed record StaffPasswordReset(string Password,bool Confirm);
public sealed record StaffRevoke(bool Confirm);

public static class StaffSecurity
{
    public const string SessionClaim="staff_session";
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Identity(ClaimsPrincipal? user,out Guid account,out Guid session)
    {
        account=Guid.Empty;session=Guid.Empty;
        return Guid.TryParse(user?.FindFirst(ClaimTypes.NameIdentifier)?.Value,out account)
            && Guid.TryParse(user?.FindFirst(SessionClaim)?.Value,out session);
    }

    public static async Task<Guid?> OpenSession(NpgsqlDataSource db,Guid account,Guid generation)
    {
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        // Serialize logins for one account and cap concurrent sessions. Clean
        // expired or superseded sessions without touching unrelated accounts.
        var rows=await NetworkManagement.Query(conn,tx,"""
            SELECT id FROM app_users WHERE id=@id AND enabled
              AND session_generation=@generation FOR UPDATE
            """,P("id",account),P("generation",generation));
        if(rows.Count!=1)return null;
        await NetworkManagement.Execute(conn,tx,"""
            DELETE FROM staff_sessions WHERE user_id=@id
            AND (expires_at<=now() OR generation<>@generation)
            """,P("id",account),P("generation",generation));
        // Keep the nine latest existing sessions; the tenth is the new login.
        await NetworkManagement.Execute(conn,tx,"""
            DELETE FROM staff_sessions WHERE id IN (
              SELECT id FROM staff_sessions WHERE user_id=@id
              ORDER BY created_at DESC,id DESC OFFSET 9)
            """,P("id",account));
        var created=await NetworkManagement.Query(conn,tx,"""
            INSERT INTO staff_sessions(user_id,generation) VALUES(@id,@generation)
            RETURNING id
            """,P("id",account),P("generation",generation));
        await tx.CommitAsync();
        return (Guid)created.Single()["id"]!;
    }

    public static async Task ValidatePrincipal(CookieValidatePrincipalContext ctx)
    {
        // Reject legacy cookies and stale claims; do not refresh permissions
        // silently. A role change requires a fresh sign-in with a new CSRF token.
        if(Identity(ctx.Principal,out var account,out var session))
        {
            var db=ctx.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
            var rows=await Store.Rows(db,"""
                SELECT u.username,u.role FROM app_users u JOIN staff_sessions s
                  ON s.user_id=u.id AND s.generation=u.session_generation
                WHERE u.id=@account AND s.id=@session AND u.enabled
                  AND s.expires_at>now()
                """,P("account",account),P("session",session));
            if(rows.Count==1 && rows[0]["username"] as string==ctx.Principal?.Identity?.Name
                && rows[0]["role"] as string==ctx.Principal?.FindFirst(ClaimTypes.Role)?.Value)
                return;
        }
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public static async Task CloseSession(HttpContext ctx,NpgsqlDataSource db)
    {
        if(Identity(ctx.User,out var account,out var session))
            await Store.Execute(db,"DELETE FROM staff_sessions WHERE id=@session AND user_id=@account",
                P("session",session),P("account",account));
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/staff/{id:guid}/access",(Guid id,HttpContext ctx,NpgsqlDataSource db,StaffAccessChange body)=>
            Change(id,ctx,db,"access",body.Confirm,body.Role,body.Enabled,null));
        api.MapPost("/staff/{id:guid}/password",(Guid id,HttpContext ctx,NpgsqlDataSource db,StaffPasswordReset body)=>
            Change(id,ctx,db,"password",body.Confirm,null,null,body.Password));
        api.MapPost("/staff/{id:guid}/revoke-sessions",(Guid id,HttpContext ctx,NpgsqlDataSource db,StaffRevoke body)=>
            Change(id,ctx,db,"revoke",body.Confirm,null,null,null));
    }

    private static async Task<IResult> Change(Guid id,HttpContext ctx,NpgsqlDataSource db,
        string action,bool confirm,string? role,bool? enabled,string? password)
    {
        if(!ctx.User.IsInRole("admin")||!Identity(ctx.User,out var actor,out var session))
            return Results.Problem("Administrator permissions required",statusCode:403);
        if(!confirm)return Results.BadRequest(new{error="Explicit confirmation required"});
        if(id==actor)return Results.BadRequest(new{error="Use a different administrator to change your own account"});
        if(action=="access" && (role is not ("admin" or "dispatcher" or "technician" or "viewer") || enabled is null))
            return Results.BadRequest(new{error="Valid role and enabled state required"});
        if(action=="password" && (password is null || password.Length is <14 or >2048))
            return Results.BadRequest(new{error="Password must contain 14–2048 characters"});
        // Derive the password outside the transaction to minimize lock duration.
        var hash=action=="password"?Passwords.Hash(password!):null;
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        // Serialize administrative changes: two admins cannot concurrently
        // disable/demote each other and leave no enabled administrator.
        await NetworkManagement.Execute(conn,tx,"SELECT pg_advisory_xact_lock(3100314)");
        var authorized=await NetworkManagement.Query(conn,tx,"""
            SELECT u.username FROM app_users u JOIN staff_sessions s
              ON s.user_id=u.id AND s.generation=u.session_generation
            WHERE u.id=@actor AND s.id=@session AND u.enabled AND u.role='admin'
              AND s.expires_at>now() FOR UPDATE OF u,s
            """,P("actor",actor),P("session",session));
        if(authorized.Count!=1)return Results.Problem("Administrator session is no longer valid",statusCode:403);
        var target=await NetworkManagement.Query(conn,tx,
            "SELECT id FROM app_users WHERE id=@id FOR UPDATE",P("id",id));
        if(target.Count!=1)return Results.NotFound();
        if(action=="access")
            await NetworkManagement.Execute(conn,tx,"""
                UPDATE app_users SET role=@role,enabled=@enabled,
                  session_generation=gen_random_uuid() WHERE id=@id
                """,P("role",role),P("enabled",enabled),P("id",id));
        else if(action=="password")
            await NetworkManagement.Execute(conn,tx,
                "UPDATE app_users SET password_hash=@hash WHERE id=@id",P("hash",hash),P("id",id));
        else
            await NetworkManagement.Execute(conn,tx,
                "UPDATE app_users SET session_generation=gen_random_uuid() WHERE id=@id",P("id",id));
        await NetworkManagement.Execute(conn,tx,"DELETE FROM staff_sessions WHERE user_id=@id",P("id",id));
        await NetworkManagement.Audit(conn,tx,(string)authorized.Single()["username"]!,
            "staff-"+action,"staff",id.ToString());
        await tx.CommitAsync();
        return Results.Ok(new{ok=true});
    }
}
