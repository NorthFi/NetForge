// root/selfhost/src/NetForge.Api/BackupVault.cs
// Encrypted, versioned backup objects. All file reads, comparisons and uploads
// by staff require administrator permission; collector tokens are write-only.
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace NetForge.Api;

public sealed record BackupEnroll(int? NasId,Guid? EquipmentId,string Name,string Vendor,bool Confirm);
public sealed record BackupTokenChange(bool Enable,bool Confirm);

public static class BackupVault
{
    private const int MaxBytes=4*1024*1024;
    private const int MaxVersions=100;
    private static readonly UTF8Encoding StrictUtf8=new(false,true);
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher");
    private static IResult Denied()=>Results.Problem("Backup vault permissions required",statusCode:403);
    private static IResult Invalid(string msg)=>Results.BadRequest(new{error=msg});
    private static IResult Conflict(string msg)=>Results.Conflict(new{error=msg});
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    private static string Hash(byte[] value)=>Convert.ToHexString(SHA256.HashData(value));
    private static string TokenHash(string token)=>Hash(Encoding.ASCII.GetBytes(token));
    private static IDataProtector Protector(IDataProtectionProvider dp,Guid profile,Guid version)=>
        dp.CreateProtector("NetForge.BackupVault.v1",profile.ToString(),version.ToString());
    private static bool SafeFilename(string? name)=>name is not null && name.Length is >=1 and <=160
        && name[0]!='.' && name.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '_' or '-');
    private static bool Kind(string? kind)=>kind is "text" or "binary";

    public static void Map(RouteGroupBuilder api,WebApplication app)
    {
        api.MapGet("/backups/profiles",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.id,p.name,p.vendor,p.nas_id,p.equipment_id,
                  p.token_hash IS NOT NULL AS collector_enabled,p.last_received_at,p.created_at,
                  (SELECT count(*) FROM backup_versions v WHERE v.profile_id=p.id) AS versions,
                  (SELECT COALESCE(sum(size_bytes),0) FROM backup_versions v WHERE v.profile_id=p.id) AS bytes
                FROM backup_profiles p ORDER BY name,id LIMIT 2000
                """));
        });
        api.MapPost("/backups/profiles",async(HttpContext ctx,NpgsqlDataSource db,BackupEnroll input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=(input.Name??"").Trim();
            if(!input.Confirm||name.Length is <1 or >160||
                (input.NasId is null)==(input.EquipmentId is null)||
                input.Vendor is not ("routeros" or "cambium" or "ubiquiti" or "mimosa" or "other"))
                return Invalid("Confirm one inventory asset, a name and vendor");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO backup_profiles(nas_id,equipment_id,name,vendor)
                VALUES(@nas,@equipment,@name,@vendor) RETURNING id
                """,P("nas",input.NasId),P("equipment",input.EquipmentId),P("name",name),P("vendor",input.Vendor));
            var id=(Guid)rows.Single()["id"]!;
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"backup-profile-create","backup_profile",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/backups/profiles/"+id,new{id});
        });
        api.MapPost("/backups/profiles/{id:guid}/token",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,BackupTokenChange input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm token rotation or revocation");
            var token=input.Enable?Convert.ToHexString(RandomNumberGenerator.GetBytes(32)):null;
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,tx,"""
                UPDATE backup_profiles SET token_hash=@hash WHERE id=@id RETURNING id
                """,P("hash",token is null?null:TokenHash(token)),P("id",id));
            if(rows.Count==0)return Results.NotFound();
            await NetworkManagement.Audit(conn,tx,Actor(ctx),input.Enable?"backup-token-rotate":"backup-token-revoke",
                "backup_profile",id.ToString());
            await tx.CommitAsync();
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{token,enabled=input.Enable});
        });
        api.MapGet("/backups/profiles/{id:guid}/versions",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(await Store.Rows(db,"""
                SELECT id,kind,filename,size_bytes,sha256,source,actor,created_at
                FROM backup_versions WHERE profile_id=@id ORDER BY created_at DESC,id DESC LIMIT 100
                """,P("id",id)));
        });
        api.MapPost("/backups/profiles/{id:guid}/upload",
            (Guid id,HttpContext ctx,NpgsqlDataSource db,IDataProtectionProvider dp,string filename,string kind,bool confirm)=>
                Admin(ctx)?(confirm?Upload(id,ctx,db,dp,filename,kind,false):Task.FromResult(Invalid("Confirm upload")))
                    :Task.FromResult(Denied())).RequireRateLimiting("backup-upload");
        // Anonymous only at ASP.NET auth layer; the random per-profile bearer
        // token is required and is checked again after obtaining the row lock.
        app.MapPost("/api/backup-ingest/{id:guid}",
            (Guid id,HttpContext ctx,NpgsqlDataSource db,IDataProtectionProvider dp,string filename,string kind)=>
                Upload(id,ctx,db,dp,filename,kind,true))
            .AllowAnonymous().RequireRateLimiting("backup-upload");
        api.MapGet("/backups/profiles/{id:guid}/versions/{version:guid}/download",
            async(Guid id,Guid version,HttpContext ctx,NpgsqlDataSource db,IDataProtectionProvider dp)=>
        {
            if(!Admin(ctx))return Denied();
            var rows=await Load(db,id,version);
            if(rows.Count==0)return Results.NotFound();
            var row=rows.Single();
            var bytes=Decrypt(dp,id,version,row);
            if(bytes is null)return Conflict("Backup integrity or encryption key validation failed");
            await Store.Audit(db,Actor(ctx),"backup-download","backup_version",version.ToString());
            ctx.Response.Headers.CacheControl="no-store";
            ctx.Response.Headers.XContentTypeOptions="nosniff";
            return Results.File(bytes,"application/octet-stream",(string)row["filename"]!);
        });
        api.MapGet("/backups/profiles/{id:guid}/compare",async(Guid id,Guid older,Guid newer,
            HttpContext ctx,NpgsqlDataSource db,IDataProtectionProvider dp)=>
        {
            if(!Admin(ctx))return Denied();
            if(older==newer)return Invalid("Select two different versions");
            var a=await Load(db,id,older);var b=await Load(db,id,newer);
            if(a.Count==0||b.Count==0)return Results.NotFound();
            if(a[0]["kind"] as string!="text"||b[0]["kind"] as string!="text")
                return Invalid("Comparison is available for UTF-8 text backups only");
            var x=Decrypt(dp,id,older,a[0]);var y=Decrypt(dp,id,newer,b[0]);
            if(x is null||y is null)return Conflict("Backup integrity or encryption key validation failed");
            // Bounded, order-aware contiguous changed range, not a minimal
            // edit script. Preserve ordering because RouterOS rule order matters.
            var left=StrictUtf8.GetString(x).Replace("\r\n","\n").Split('\n');
            var right=StrictUtf8.GetString(y).Replace("\r\n","\n").Split('\n');
            var prefix=0;while(prefix<left.Length&&prefix<right.Length&&left[prefix]==right[prefix])prefix++;
            var suffix=0;while(suffix<left.Length-prefix&&suffix<right.Length-prefix&&
                left[left.Length-1-suffix]==right[right.Length-1-suffix])suffix++;
            var removed=left.Length-prefix-suffix;var added=right.Length-prefix-suffix;
            static string[] Preview(string[] lines,int start,int count)=>lines.Skip(start).Take(Math.Min(count,200))
                .Select(s=>s.Length>1024?s[..1024]+" [truncated]":s).ToArray();
            await Store.Audit(db,Actor(ctx),"backup-compare","backup_profile",id.ToString());
            ctx.Response.Headers.CacheControl="no-store";
            return Results.Ok(new{byteIdentical=x.AsSpan().SequenceEqual(y),unchangedPrefix=prefix,unchangedSuffix=suffix,
                olderChangedLines=removed,newerChangedLines=added,
                olderPreview=Preview(left,prefix,removed),newerPreview=Preview(right,prefix,added),
                truncated=removed>200||added>200});
        });
    }

    private static Task<List<Dictionary<string,object?>>> Load(NpgsqlDataSource db,Guid profile,Guid version)=>
        Store.Rows(db,"""
            SELECT kind,filename,size_bytes,sha256,ciphertext FROM backup_versions
            WHERE profile_id=@profile AND id=@id
            """,P("profile",profile),P("id",version));

    private static byte[]? Decrypt(IDataProtectionProvider dp,Guid profile,Guid version,Dictionary<string,object?> row)
    {
        try
        {
            var bytes=Protector(dp,profile,version).Unprotect((byte[])row["ciphertext"]!);
            return bytes.Length==(int)row["size_bytes"]!&&Hash(bytes)==((string)row["sha256"]!).Trim()?bytes:null;
        }
        catch(CryptographicException){return null;}
    }

    private static bool TokenMatches(string? token,object? stored)=>
        token is not null && stored is string hash &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(TokenHash(token)),Encoding.ASCII.GetBytes(hash.Trim()));

    private static async Task<IResult> Upload(Guid id,HttpContext ctx,NpgsqlDataSource db,
        IDataProtectionProvider dp,string filename,string kind,bool collector)
    {
        ctx.Response.Headers.CacheControl="no-store";
        if(!SafeFilename(filename)||!Kind(kind))return Invalid("Filename must be a safe basename; kind must be text or binary");
        string? token=null;
        if(collector)
        {
            var auth=ctx.Request.Headers.Authorization.ToString();
            if(!auth.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)||auth.Length!=71)return Results.Unauthorized();
            token=auth[7..];
            if(!token.All(char.IsAsciiHexDigit))return Results.Unauthorized();
            var initial=await Store.Rows(db,"SELECT token_hash FROM backup_profiles WHERE id=@id",P("id",id));
            if(initial.Count!=1||!TokenMatches(token,initial[0]["token_hash"]))return Results.Unauthorized();
        }
        var length=ctx.Request.ContentLength;
        if(length is null or <=0 or >MaxBytes ||
            !string.Equals(ctx.Request.ContentType,"application/octet-stream",StringComparison.OrdinalIgnoreCase))
            return Invalid("Send application/octet-stream with Content-Length between 1 and 4194304");
        byte[] bytes=new byte[(int)length.Value];
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ctx.Request.Body.ReadExactlyAsync(bytes,timeout.Token);
        }
        catch(OperationCanceledException){return Results.StatusCode(408);}
        catch(EndOfStreamException){return Invalid("Incomplete file");}
        if(kind=="text")
        {
            try{if(StrictUtf8.GetString(bytes).Contains('\0'))return Invalid("Text backup contains NUL bytes");}
            catch(DecoderFallbackException){return Invalid("Text backup must be valid UTF-8");}
        }
        var digest=Hash(bytes);
        await using var conn=await db.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        var profiles=await NetworkManagement.Query(conn,tx,
            "SELECT token_hash,last_received_at FROM backup_profiles WHERE id=@id FOR UPDATE",P("id",id));
        if(profiles.Count==0)return collector?Results.Unauthorized():Results.NotFound();
        if(collector&&!TokenMatches(token,profiles[0]["token_hash"]))return Results.Unauthorized();
        var prior=await NetworkManagement.Query(conn,tx,"""
            SELECT id,sha256 FROM backup_versions WHERE profile_id=@id AND kind=@kind
            ORDER BY created_at DESC,id DESC LIMIT 1
            """,P("id",id),P("kind",kind));
        if(prior.Count==1&&((string)prior[0]["sha256"]!).Trim()==digest)
        {
            await NetworkManagement.Execute(conn,tx,"UPDATE backup_profiles SET last_received_at=now() WHERE id=@id",P("id",id));
            await tx.CommitAsync();
            return Results.Ok(new{id=prior[0]["id"],sha256=digest,duplicate=true});
        }
        if(collector&&profiles[0]["last_received_at"] is DateTime last&&last>DateTime.UtcNow.AddSeconds(-60))
            return Results.StatusCode(429);
        var count=await NetworkManagement.Query(conn,tx,"SELECT count(*) AS n FROM backup_versions WHERE profile_id=@id",P("id",id));
        if((long)count[0]["n"]!>=MaxVersions)return Conflict("Vault profile has reached 100 versions; no automatic deletion");
        var version=Guid.NewGuid();
        var actor=collector?"collector":Actor(ctx);
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO backup_versions(id,profile_id,kind,filename,size_bytes,sha256,ciphertext,source,actor)
            VALUES(@version,@id,@kind,@filename,@size,@hash,@cipher,@source,@actor)
            """,P("version",version),P("id",id),P("kind",kind),P("filename",filename),P("size",bytes.Length),
            P("hash",digest),P("cipher",Protector(dp,id,version).Protect(bytes)),P("source",collector?"collector":"manual"),P("actor",actor));
        await NetworkManagement.Execute(conn,tx,"UPDATE backup_profiles SET last_received_at=now() WHERE id=@id",P("id",id));
        await NetworkManagement.Audit(conn,tx,actor,"backup-upload","backup_version",version.ToString());
        await tx.CommitAsync();
        return Results.Created("/api/backups/profiles/"+id+"/versions/"+version,new{id=version,sha256=digest,duplicate=false});
    }
}
