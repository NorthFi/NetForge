// root/selfhost/src/NetForge.Api/NetworkExecution.cs
// Device-scoped pull agent. No SSH credential, arbitrary destination or raw
// command output is accepted by the server. Claim once, never retry execution.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace NetForge.Api;

public sealed record ExecutionAgentInput(bool Enable,bool Confirm);
public sealed record ExecutionClaim(string Address);
public sealed record ExecutionReport(string Claim,string Result,int OutputBytes,int? ExitCode);

public static class NetworkExecution
{
    private static NpgsqlParameter P(string key,object? value)=>Store.P(key,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    private static IResult Conflict(string msg)=>Results.Conflict(new{error=msg});
    private static IResult Invalid(string msg)=>Results.BadRequest(new{error=msg});
    private static string Token()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool TokenFormat(string? token)=>token is {Length:64}&&token.All(char.IsAsciiHexDigit);
    private static bool Matches(string? token,object? hash)=>TokenFormat(token)&&hash is string stored&&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token!)),Encoding.ASCII.GetBytes(stored.Trim()));
    private static string? Bearer(HttpContext ctx)
    {
        var h=ctx.Request.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)&&h.Length==71?h[7..]:null;
    }
    // The runner supports pre-reviewed single-line RouterOS commands on older
    // firmware. Hash checks are performed again by the independently configured agent.
    internal static bool ValidBody(string body)=>body.Length is >0 and <=4096 &&
        (body.StartsWith('/')||body.StartsWith(':')) && body.All(c=>c is >= ' ' and <= '~');
    private static bool ValidAddress(string? value)=>IPAddress.TryParse(value,out var ip)&&
        ip.AddressFamily==AddressFamily.InterNetwork&&ip.ToString()==value&&
        ip.GetAddressBytes()[0] is >0 and <224 and not 127&&
        !value!.StartsWith("169.254.",StringComparison.Ordinal)&&value!="100.100.100.200";
    private static Task<List<Dictionary<string,object?>>> Source(NpgsqlConnection conn,NpgsqlTransaction tx,Guid profile)=>
        NetworkManagement.Query(conn,tx,"""
            SELECT p.vendor,COALESCE(n.nasname,host(e.management_ip)) AS current_address,
                   a.token_hash,a.enabled,host(a.address) AS address
            FROM backup_profiles p LEFT JOIN nas n ON n.id=p.nas_id
            LEFT JOIN equipment e ON e.id=p.equipment_id
            LEFT JOIN network_execution_agents a ON a.profile_id=p.id
            WHERE p.id=@id FOR UPDATE OF p
            """,P("id",profile));
    private static bool Ready(Dictionary<string,object?> row)=>row["vendor"] as string=="routeros"&&
        row["enabled"] is true&&row["address"] as string==row["current_address"] as string&&
        ValidAddress(row["address"] as string);
    internal static async Task<bool> Ready(NpgsqlConnection conn,NpgsqlTransaction tx,Guid profile)
    {
        var rows=await Source(conn,tx,profile);
        return rows.Count==1&&Ready(rows[0]);
    }
    public static void Map(RouteGroupBuilder api,WebApplication app)
    {
        api.MapGet("/changes/agents",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Admin(ctx)&&!ctx.User.IsInRole("dispatcher"))return Results.StatusCode(403);
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.id AS profile_id,p.name,p.vendor,host(a.address) AS address,a.enabled,
                    a.created_at FROM backup_profiles p JOIN network_execution_agents a ON a.profile_id=p.id
                ORDER BY p.name LIMIT 2000
                """));
        });
        api.MapPost("/changes/agents/{profile:guid}/token",async(Guid profile,HttpContext ctx,
            NpgsqlDataSource db,ExecutionAgentInput input)=>
        {
            if(!Admin(ctx))return Results.StatusCode(403);
            if(!input.Confirm)return Invalid("Confirm execution agent enrollment or revocation");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await Source(conn,tx,profile);if(rows.Count!=1)return Results.NotFound();
            if((await NetworkManagement.Query(conn,tx,"""
                SELECT t.id FROM network_change_targets t JOIN network_execution_jobs j ON j.target_id=t.id
                WHERE t.profile_id=@id AND t.state='handed_off' LIMIT 1
                """,P("id",profile))).Count!=0)return Conflict("Resolve outstanding execution before rotating or revoking the token");
            string? token=null;
            if(input.Enable)
            {
                var address=rows[0]["current_address"] as string;
                if(rows[0]["vendor"] as string!="routeros"||!ValidAddress(address))
                    return Conflict("An inventoried RouterOS IPv4 management address is required");
                token=Token();
                await NetworkManagement.Execute(conn,tx,"""
                    INSERT INTO network_execution_agents(profile_id,address,token_hash,enabled)
                    VALUES(@id,@ip::inet,@hash,true)
                    ON CONFLICT(profile_id) DO UPDATE SET address=EXCLUDED.address,
                        token_hash=EXCLUDED.token_hash,enabled=true,created_at=now()
                    """,P("id",profile),P("ip",address),P("hash",Hash(token)));
            }
            else await NetworkManagement.Execute(conn,tx,"UPDATE network_execution_agents SET enabled=false,token_hash=@hash WHERE profile_id=@id",P("id",profile),P("hash",Hash(Token())));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),input.Enable?"execution-agent-enable":"execution-agent-revoke","backup_profile",profile.ToString());
            await tx.CommitAsync();ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{token,enabled=input.Enable});
        });
        app.MapPost("/api/execution-agent/{profile:guid}/claim",Claim).AllowAnonymous().RequireRateLimiting("execution-agent");
        app.MapPost("/api/execution-agent/{profile:guid}/jobs/{target:guid}/report",Report).AllowAnonymous().RequireRateLimiting("execution-agent");
    }
    private static async Task<IResult> Claim(Guid profile,HttpContext ctx,NpgsqlDataSource db,
        IDataProtectionProvider dp,ExecutionClaim input)
    {
        ctx.Response.Headers.CacheControl="no-store";
        var token=Bearer(ctx);if(!TokenFormat(token))return Results.Unauthorized();
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        // Authenticate before looking at jobs. Acquire locks in the same order
        // as manual handoff: plan, then backup profile. Reauthenticate under lock.
        var auth=await NetworkManagement.Query(conn,tx,"SELECT token_hash,enabled FROM network_execution_agents WHERE profile_id=@id",P("id",profile));
        if(auth.Count!=1||auth[0]["enabled"] is not true||!Matches(token,auth[0]["token_hash"]))return Results.Unauthorized();
        var queued=await NetworkManagement.Query(conn,tx,"""
            SELECT t.plan_id,t.id FROM network_change_targets t JOIN network_execution_jobs j ON j.target_id=t.id
            WHERE t.profile_id=@id AND t.state='handed_off' AND j.state='queued'
            """,P("id",profile));
        if(queued.Count!=1)return Results.NoContent();
        var target=(Guid)queued[0]["id"]!;var plan=(Guid)queued[0]["plan_id"]!;
        var plans=await NetworkManagement.Query(conn,tx,"SELECT state,version_id,window_start,window_end FROM network_change_plans WHERE id=@id FOR UPDATE",P("id",plan));
        var source=await Source(conn,tx,profile);
        if(source.Count!=1||!Matches(token,source[0]["token_hash"])||source[0]["enabled"] is not true)return Results.Unauthorized();
        if(!Ready(source[0])||input.Address!=source[0]["address"] as string)return Conflict("Inventory or local agent address mismatch");
        var now=DateTime.UtcNow;
        if(plans.Count!=1||plans[0]["state"] as string!="running"||(DateTime)plans[0]["window_start"]!>now||
           (DateTime)plans[0]["window_end"]!<now.AddMinutes(2))return Conflict("Maintenance window no longer has two minutes remaining");
        var gate=await NetworkManagement.Query(conn,tx,"""
            SELECT b.id,b.size_bytes,b.sha256,b.ciphertext FROM network_change_targets t
              JOIN network_execution_jobs j ON j.target_id=t.id
              JOIN backup_versions b ON b.id=t.backup_id AND b.profile_id=t.profile_id
            WHERE t.id=@target AND t.state='handed_off' AND j.state='queued'
              AND j.queued_at>=now()-interval '5 minutes' AND b.created_at>=now()-interval '24 hours'
            """,P("target",target));
        if(gate.Count!=1)return Conflict("Queue or backup is stale, or job has already been claimed");
        try
        {
            var backup=gate[0];
            var raw=dp.CreateProtector("NetForge.BackupVault.v1",profile.ToString(),((Guid)backup["id"]!).ToString()).Unprotect((byte[])backup["ciphertext"]!);
            if(raw.Length!=(int)backup["size_bytes"]!||Convert.ToHexString(SHA256.HashData(raw))!=((string)backup["sha256"]!).Trim())
                return Conflict("Backup integrity failed");
            var version=(Guid)plans[0]["version_id"]!;
            var scripts=await NetworkManagement.Query(conn,tx,"SELECT ciphertext,sha256 FROM network_script_versions WHERE id=@id",P("id",version));
            var body=dp.CreateProtector("NetForge.NetworkScript.v1",version.ToString()).Unprotect((byte[])scripts[0]["ciphertext"]!);
            var hash=Convert.ToHexString(SHA256.HashData(body));var command=Encoding.UTF8.GetString(body);
            if(hash!=((string)scripts[0]["sha256"]!).Trim()||!ValidBody(command))return Conflict("Script integrity or format failed");
            var claim=Token();var deadline=now.AddMinutes(2);
            var changed=await NetworkManagement.Query(conn,tx,"""
                UPDATE network_execution_jobs SET state='claimed',claim_hash=@hash,claimed_at=now(),deadline=@deadline
                WHERE target_id=@target AND state='queued' RETURNING target_id
                """,P("hash",Hash(claim)),P("deadline",deadline),P("target",target));
            if(changed.Count!=1)return Conflict("Job already claimed");
            await NetworkManagement.Audit(conn,tx,"execution-agent","execution-claim","network_target",target.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{targetId=target,profileId=profile,address=input.Address,claim,deadline,command,sha256=hash});
        }
        catch(CryptographicException){return Conflict("Backup or script encryption key unavailable");}
    }
    private static async Task<IResult> Report(Guid profile,Guid target,HttpContext ctx,NpgsqlDataSource db,ExecutionReport input)
    {
        ctx.Response.Headers.CacheControl="no-store";
        var token=Bearer(ctx);
        if(!TokenFormat(token)||!TokenFormat(input.Claim))return Results.Unauthorized();
        if(input.Result is not ("exit_zero" or "nonzero" or "timeout" or "refused" or "output_limit" or "transport_error")||
            input.OutputBytes is <0 or >65536||
            (input.Result=="exit_zero"&&input.ExitCode!=0)||
            (input.Result=="nonzero"&&(input.ExitCode is null or 0)))return Invalid("Invalid execution report");
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var auth=await NetworkManagement.Query(conn,tx,"SELECT token_hash,enabled FROM network_execution_agents WHERE profile_id=@id",P("id",profile));
        if(auth.Count!=1||auth[0]["enabled"] is not true||!Matches(token,auth[0]["token_hash"]))return Results.Unauthorized();
        var rows=await NetworkManagement.Query(conn,tx,"SELECT plan_id FROM network_change_targets WHERE id=@id AND profile_id=@profile",P("id",target),P("profile",profile));
        if(rows.Count!=1)return Results.NotFound();
        await NetworkManagement.Query(conn,tx,"SELECT id FROM network_change_plans WHERE id=@id FOR UPDATE",P("id",rows[0]["plan_id"]));
        var source=await Source(conn,tx,profile);
        if(source.Count!=1||source[0]["enabled"] is not true||!Matches(token,source[0]["token_hash"]))return Results.Unauthorized();
        var jobs=await NetworkManagement.Query(conn,tx,"SELECT state,claim_hash,deadline FROM network_execution_jobs WHERE target_id=@id FOR UPDATE",P("id",target));
        if(jobs.Count!=1||!Matches(input.Claim,jobs[0]["claim_hash"]))return Results.Unauthorized();
        if(jobs[0]["state"] as string=="reported")return Results.Ok(new{duplicate=true});
        if(jobs[0]["state"] as string!="claimed")return Conflict("Job is no longer claimed");
        var status=(DateTime)jobs[0]["deadline"]!<DateTime.UtcNow?"uncertain":input.Result;
        await NetworkManagement.Execute(conn,tx,"""
            UPDATE network_execution_jobs SET state='reported',result=@result,output_bytes=@bytes,exit_code=@code,reported_at=now()
            WHERE target_id=@target
            """,P("result",status),P("bytes",input.OutputBytes),P("code",input.ExitCode),P("target",target));
        await NetworkManagement.Audit(conn,tx,"execution-agent","execution-report:"+status,"network_target",target.ToString());
        await tx.CommitAsync();
        // This is transport evidence only, not configuration verification.
        return Results.Ok(new{duplicate=false,result=status});
    }
}
