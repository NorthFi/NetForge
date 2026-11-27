// root/selfhost/src/NetForge.Api/NetworkChanges.cs
// Human-executed network changes. No transport, command runner or automatic
// restore. Independent approval, frozen targets and backup gates are enforced
// server-side, under a plan row lock, not just by browser controls.
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace NetForge.Api;

public sealed record NetworkScriptInput(string Name,string Vendor,string? Description);
public sealed record NetworkRevisionInput(string Body,bool Confirm);
public sealed record NetworkGroupInput(string Name,Guid[] ProfileIds);
public sealed record NetworkMembersInput(Guid[] ProfileIds,bool Confirm);
public sealed record NetworkPlanInput(Guid VersionId,Guid GroupId,Guid CanaryProfileId,
    string Reason,DateTimeOffset WindowStart,DateTimeOffset WindowEnd,bool Confirm);
public sealed record NetworkChangeConfirm(bool Confirm);
public sealed record NetworkOutcome(bool Success,string Note,bool Confirm);

public static class NetworkChanges
{
    private static NpgsqlParameter P(string key,object? value)=>Store.P(key,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher");
    private static Guid UserId(HttpContext ctx)=>Guid.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    private static IResult Denied()=>Results.Problem("Network change permissions required",statusCode:403);
    private static IResult Invalid(string message)=>Results.BadRequest(new{error=message});
    private static IResult Conflict(string message)=>Results.Conflict(new{error=message});
    private static IDataProtector Cipher(IDataProtectionProvider dp,Guid version)=>
        dp.CreateProtector("NetForge.NetworkScript.v1",version.ToString());
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static bool Vendor(string? v)=>v is "routeros" or "cambium" or "ubiquiti" or "mimosa" or "other";
    private static bool Members(Guid[]? ids)=>ids is {Length:>=1 and <=50} &&
        ids.All(x=>x!=Guid.Empty) && ids.Distinct().Count()==ids.Length;
    private static Task<List<Dictionary<string,object?>>> Plan(NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)=>
        NetworkManagement.Query(conn,tx,"SELECT * FROM network_change_plans WHERE id=@id FOR UPDATE",P("id",id));
    private static bool InWindow(Dictionary<string,object?> row)=>
        (DateTime)row["window_start"]!<=DateTime.UtcNow && (DateTime)row["window_end"]!>DateTime.UtcNow;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/changes/scripts",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT s.id,s.name,s.vendor,s.description,s.created_at,
                  (SELECT count(*) FROM network_script_versions v WHERE v.script_id=s.id) AS revisions
                FROM network_scripts s ORDER BY name LIMIT 1000
                """));
        });
        api.MapPost("/changes/scripts",async(HttpContext ctx,NpgsqlDataSource db,NetworkScriptInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=(input.Name??"").Trim();var desc=(input.Description??"").Trim();
            if(name.Length is <1 or >160||desc.Length>500||!Vendor(input.Vendor))return Invalid("Invalid script metadata");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO network_scripts(name,vendor,description) VALUES(@name,@vendor,@desc) RETURNING id
                """,P("name",name),P("vendor",input.Vendor),P("desc",desc));
            var id=(Guid)rows.Single()["id"]!;
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-script-create","network_script",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/changes/scripts/"+id,new{id});
        });
        api.MapGet("/changes/scripts/{id:guid}/versions",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT id,revision,sha256,created_at,created_by FROM network_script_versions
                WHERE script_id=@id ORDER BY revision DESC LIMIT 100
                """,P("id",id)));
        });
        api.MapPost("/changes/scripts/{id:guid}/versions",async(Guid id,HttpContext ctx,NpgsqlDataSource db,
            IDataProtectionProvider dp,NetworkRevisionInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm||string.IsNullOrWhiteSpace(input.Body)||input.Body.Length>65536||input.Body.Contains('\0'))
                return Invalid("Confirm a nonempty script of at most 65536 characters");
            var bytes=Encoding.UTF8.GetBytes(input.Body);
            if(bytes.Length>65536)return Invalid("Script exceeds 64 KiB");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var script=await NetworkManagement.Query(conn,tx,"SELECT id FROM network_scripts WHERE id=@id FOR UPDATE",P("id",id));
            if(script.Count==0)return Results.NotFound();
            var count=await NetworkManagement.Query(conn,tx,"SELECT COALESCE(max(revision),0) AS n FROM network_script_versions WHERE script_id=@id",P("id",id));
            var revision=(int)count.Single()["n"]!+1;
            if(revision>100)return Conflict("Script has reached 100 immutable revisions");
            var version=Guid.NewGuid();var hash=Digest(bytes);
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO network_script_versions(id,script_id,revision,sha256,ciphertext,created_by)
                VALUES(@version,@id,@rev,@hash,@cipher,@actor)
                """,P("version",version),P("id",id),P("rev",revision),P("hash",hash),
                P("cipher",Cipher(dp,version).Protect(bytes)),P("actor",UserId(ctx)));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-script-version","network_script_version",version.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/changes/scripts/"+id+"/versions",new{id=version,revision,sha256=hash});
        });
        api.MapGet("/changes/versions/{id:guid}/review",async(Guid id,HttpContext ctx,NpgsqlDataSource db,IDataProtectionProvider dp)=>
        {
            if(!Admin(ctx))return Denied();
            var rows=await Store.Rows(db,"SELECT ciphertext,sha256 FROM network_script_versions WHERE id=@id",P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var body=Read(dp,id,rows.Single());
            if(body is null)return Conflict("Script integrity or encryption key validation failed");
            await Store.Audit(db,Actor(ctx),"network-script-review","network_script_version",id.ToString());
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{body,sha256=((string)rows[0]["sha256"]!).Trim()});
        });
        api.MapGet("/changes/groups",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT g.id,g.name,ARRAY(SELECT m.profile_id FROM network_group_members m
                    WHERE m.group_id=g.id ORDER BY m.profile_id) AS profile_ids
                FROM network_device_groups g ORDER BY g.name LIMIT 1000
                """));
        });
        api.MapPost("/changes/groups",async(HttpContext ctx,NpgsqlDataSource db,NetworkGroupInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=(input.Name??"").Trim();
            if(name.Length is <1 or >160||!Members(input.ProfileIds))return Invalid("Name and 1–50 unique backup profiles required");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,tx,"INSERT INTO network_device_groups(name) VALUES(@name) RETURNING id",P("name",name));
            var id=(Guid)rows.Single()["id"]!;
            if(!await SetMembers(conn,tx,id,input.ProfileIds))return Conflict("Unknown backup profile");
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-group-create","network_group",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/changes/groups/"+id,new{id});
        });
        api.MapPut("/changes/groups/{id:guid}",async(Guid id,HttpContext ctx,NpgsqlDataSource db,NetworkMembersInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm||!Members(input.ProfileIds))return Invalid("Confirm 1–50 unique backup profiles");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            if((await NetworkManagement.Query(conn,tx,"SELECT id FROM network_device_groups WHERE id=@id FOR UPDATE",P("id",id))).Count==0)
                return Results.NotFound();
            if(!await SetMembers(conn,tx,id,input.ProfileIds))return Conflict("Unknown backup profile");
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-group-update","network_group",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapGet("/changes/plans",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.id,p.state,p.reason,p.window_start,p.window_end,p.created_at,p.approved_at,
                   p.created_by,p.approved_by,p.version_id,p.group_id,s.name AS script,s.vendor,v.revision,
                   u.username AS creator,a.username AS approver
                FROM network_change_plans p JOIN network_script_versions v ON v.id=p.version_id
                JOIN network_scripts s ON s.id=v.script_id JOIN app_users u ON u.id=p.created_by
                LEFT JOIN app_users a ON a.id=p.approved_by ORDER BY p.created_at DESC LIMIT 500
                """));
        });
        api.MapGet("/changes/plans/{id:guid}/targets",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT t.id,t.profile_id,p.name,p.vendor,t.ordinal,t.state,t.backup_id,
                   t.handed_off_at,t.completed_at,t.outcome_note,u.username AS operator
                FROM network_change_targets t JOIN backup_profiles p ON p.id=t.profile_id
                LEFT JOIN app_users u ON u.id=t.operator_id WHERE t.plan_id=@id ORDER BY t.ordinal
                """,P("id",id)));
        });
        api.MapPost("/changes/plans",async(HttpContext ctx,NpgsqlDataSource db,NetworkPlanInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var reason=(input.Reason??"").Trim();
            if(!input.Confirm||reason.Length is <5 or >500||input.WindowEnd<=input.WindowStart||
                input.WindowEnd<=DateTimeOffset.UtcNow||input.WindowEnd-input.WindowStart>TimeSpan.FromHours(24))
                return Invalid("Confirm a reason and future-ending maintenance window of at most 24 hours");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            if((await NetworkManagement.Query(conn,tx,"SELECT id FROM network_device_groups WHERE id=@id FOR UPDATE",P("id",input.GroupId))).Count==0)
                return Results.NotFound();
            var script=await NetworkManagement.Query(conn,tx,"""
                SELECT s.vendor FROM network_script_versions v JOIN network_scripts s ON s.id=v.script_id WHERE v.id=@id
                """,P("id",input.VersionId));
            if(script.Count==0)return Results.NotFound();
            var targets=await NetworkManagement.Query(conn,tx,"""
                SELECT p.id,p.vendor FROM network_group_members m JOIN backup_profiles p ON p.id=m.profile_id
                WHERE m.group_id=@id ORDER BY p.id
                """,P("id",input.GroupId));
            if(targets.Count is <1 or >50||targets.Any(t=>!Equals(t["vendor"],script[0]["vendor"]))||
               !targets.Any(t=>(Guid)t["id"]! ==input.CanaryProfileId))
                return Conflict("Group must contain the selected canary and only matching-vendor backup profiles");
            var plan=Guid.NewGuid();
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO network_change_plans(id,version_id,group_id,reason,created_by,window_start,window_end)
                VALUES(@id,@version,@group,@reason,@actor,@start,@end)
                """,P("id",plan),P("version",input.VersionId),P("group",input.GroupId),P("reason",reason),
                P("actor",UserId(ctx)),P("start",input.WindowStart.UtcDateTime),P("end",input.WindowEnd.UtcDateTime));
            var ordered=targets.Select(t=>(Guid)t["id"]!).OrderBy(x=>x==input.CanaryProfileId?0:1).ThenBy(x=>x).ToArray();
            for(var i=0;i<ordered.Length;i++)
                await NetworkManagement.Execute(conn,tx,"""
                    INSERT INTO network_change_targets(plan_id,profile_id,ordinal) VALUES(@plan,@profile,@ord)
                    """,P("plan",plan),P("profile",ordered[i]),P("ord",i+1));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-plan-create","network_plan",plan.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/changes/plans/"+plan,new{id=plan});
        });
        api.MapPost("/changes/plans/{id:guid}/approve",async(Guid id,HttpContext ctx,NpgsqlDataSource db,NetworkChangeConfirm input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm independent review of the exact script, targets and maintenance window");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await Plan(conn,tx,id);if(rows.Count==0)return Results.NotFound();
            var p=rows.Single();
            if(p["state"] as string!="pending"||(Guid)p["created_by"]! ==UserId(ctx)||
                (DateTime)p["window_end"]!<=DateTime.UtcNow)return Conflict("Approval requires a different administrator and an unexpired pending plan");
            await NetworkManagement.Execute(conn,tx,"""
                UPDATE network_change_plans SET state='approved',approved_by=@actor,approved_at=now() WHERE id=@id
                """,P("actor",UserId(ctx)),P("id",id));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-plan-approve","network_plan",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/changes/plans/{id:guid}/cancel",async(Guid id,HttpContext ctx,NpgsqlDataSource db,NetworkChangeConfirm input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm cancellation; already exported scripts cannot be recalled");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await Plan(conn,tx,id);if(rows.Count==0)return Results.NotFound();
            if(rows[0]["state"] as string is not ("pending" or "approved" or "running"))
                return Conflict("Plan is already terminal");
            if((await NetworkManagement.Query(conn,tx,"SELECT id FROM network_change_targets WHERE plan_id=@id AND state='handed_off'",P("id",id))).Count>0)
                return Conflict("Record the outcome of the outstanding handoff first");
            await NetworkManagement.Execute(conn,tx,"UPDATE network_change_plans SET state='cancelled' WHERE id=@id",P("id",id));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-plan-cancel","network_plan",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/changes/plans/{id:guid}/targets/{target:guid}/handoff",Handoff);
        api.MapPost("/changes/plans/{id:guid}/targets/{target:guid}/outcome",Outcome);
    }

    private static async Task<bool> SetMembers(NpgsqlConnection conn,NpgsqlTransaction tx,Guid id,Guid[] ids)
    {
        var found=await NetworkManagement.Query(conn,tx,"SELECT id FROM backup_profiles WHERE id=ANY(@ids)",P("ids",ids));
        if(found.Count!=ids.Length)return false;
        await NetworkManagement.Execute(conn,tx,"DELETE FROM network_group_members WHERE group_id=@id",P("id",id));
        foreach(var profile in ids)
            await NetworkManagement.Execute(conn,tx,"INSERT INTO network_group_members(group_id,profile_id) VALUES(@id,@profile)",P("id",id),P("profile",profile));
        return true;
    }
    private static string? Read(IDataProtectionProvider dp,Guid id,Dictionary<string,object?> row)
    {
        try
        {
            var bytes=Cipher(dp,id).Unprotect((byte[])row["ciphertext"]!);
            return Digest(bytes)==((string)row["sha256"]!).Trim()?Encoding.UTF8.GetString(bytes):null;
        }
        catch(CryptographicException){return null;}
    }
    private static async Task<IResult> Handoff(Guid id,Guid target,HttpContext ctx,NpgsqlDataSource db,
        IDataProtectionProvider dp,NetworkChangeConfirm input)
    {
        if(!Admin(ctx))return Denied();
        if(!input.Confirm)return Invalid("Confirm manual execution on this exact target");
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var plans=await Plan(conn,tx,id);if(plans.Count==0)return Results.NotFound();
        var p=plans.Single();
        if(p["state"] as string is not ("approved" or "running")||!InWindow(p))
            return Conflict("Approved plan and active maintenance window required");
        // One target at a time, canary first. No later target after any failure.
        var next=await NetworkManagement.Query(conn,tx,"""
            SELECT id,profile_id,state FROM network_change_targets
            WHERE plan_id=@id AND state<>'succeeded' ORDER BY ordinal LIMIT 1
            """,P("id",id));
        if(next.Count!=1||(Guid)next[0]["id"]! !=target||next[0]["state"] as string!="pending")
            return Conflict("Only the next pending target can be handed off; record any outstanding outcome");
        var profile=(Guid)next[0]["profile_id"]!;
        var backups=await NetworkManagement.Query(conn,tx,"""
            SELECT id,size_bytes,sha256,ciphertext FROM backup_versions
            WHERE profile_id=@profile AND created_at>=now()-interval '24 hours'
            ORDER BY created_at DESC,id DESC LIMIT 1
            """,P("profile",profile));
        if(backups.Count==0)return Conflict("A stored backup version created within the last 24 hours is required");
        var backup=backups.Single();var backupId=(Guid)backup["id"]!;
        // Match the vault's purpose chain and validate original length/hash.
        // Fresh metadata alone is not sufficient when keys or files are damaged.
        try
        {
            var bytes=dp.CreateProtector("NetForge.BackupVault.v1",profile.ToString(),backupId.ToString())
                .Unprotect((byte[])backup["ciphertext"]!);
            if(bytes.Length!=(int)backup["size_bytes"]!||Digest(bytes)!=((string)backup["sha256"]!).Trim())
                return Conflict("Backup integrity check failed");
        }
        catch(CryptographicException){return Conflict("Backup encryption key unavailable");}
        var version=(Guid)p["version_id"]!;
        var scripts=await NetworkManagement.Query(conn,tx,"SELECT ciphertext,sha256 FROM network_script_versions WHERE id=@id",P("id",version));
        var body=Read(dp,version,scripts.Single());
        if(body is null)return Conflict("Script integrity or encryption key validation failed");
        await NetworkManagement.Execute(conn,tx,"""
            UPDATE network_change_targets SET state='handed_off',backup_id=@backup,operator_id=@actor,handed_off_at=now() WHERE id=@target
            """,P("backup",backupId),P("actor",UserId(ctx)),P("target",target));
        await NetworkManagement.Execute(conn,tx,"UPDATE network_change_plans SET state='running' WHERE id=@id",P("id",id));
        await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-target-handoff","network_target",target.ToString());
        await tx.CommitAsync();
        ctx.Response.Headers.CacheControl="no-store";
        return Results.Ok(new{body,backupId,versionId=version,sha256=((string)scripts[0]["sha256"]!).Trim(),
            note="Manual handoff only. Verify target identity and rollback procedure. NetForge has not executed this script."});
    }
    private static async Task<IResult> Outcome(Guid id,Guid target,HttpContext ctx,NpgsqlDataSource db,NetworkOutcome input)
    {
        if(!Admin(ctx))return Denied();
        var note=(input.Note??"").Trim();
        if(!input.Confirm||note.Length is <5 or >500)return Invalid("Confirm the operator-attested outcome with a 5–500 character note; do not include secrets");
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var plans=await Plan(conn,tx,id);if(plans.Count==0)return Results.NotFound();
        if(plans[0]["state"] as string!="running")return Conflict("Plan is not running");
        var changed=await NetworkManagement.Query(conn,tx,"""
            UPDATE network_change_targets SET state=@state,outcome_note=@note,completed_at=now()
            WHERE id=@target AND plan_id=@id AND state='handed_off' AND operator_id=@actor RETURNING id
            """,P("state",input.Success?"succeeded":"failed"),P("note",note),P("target",target),P("id",id),P("actor",UserId(ctx)));
        if(changed.Count!=1)return Conflict("Only the operator who received the script may attest the outcome");
        var pending=await NetworkManagement.Query(conn,tx,"SELECT id FROM network_change_targets WHERE plan_id=@id AND state<>'succeeded' LIMIT 1",P("id",id));
        var state=!input.Success?"halted":pending.Count==0?"completed":"running";
        await NetworkManagement.Execute(conn,tx,"UPDATE network_change_plans SET state=@state WHERE id=@id",P("state",state),P("id",id));
        await NetworkManagement.Audit(conn,tx,Actor(ctx),"network-target-"+(input.Success?"success":"failure"),"network_target",target.ToString());
        await tx.CommitAsync();
        return Results.Ok(new{state});
    }
}
