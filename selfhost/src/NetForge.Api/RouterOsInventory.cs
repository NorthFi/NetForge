// root/selfhost/src/NetForge.Api/RouterOsInventory.cs
// Read-only RouterOS v7 REST inventory. A literal saved address, operator
// allowlist, and pinned TLS certificate bind credentials to a single router.
// Only selected GET endpoints and allowlisted fields ever reach the database.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace NetForge.Api;

public sealed record RouterOsEnroll(int? NasId,Guid? EquipmentId,string Name,int Port,
    string Username,string Password,string CertificateSha256,bool Confirm);
public sealed record RouterOsCredentials(string Username,string Password,string CertificateSha256,bool Confirm);
public sealed record RouterOsEnabled(bool Enabled,bool Confirm);
public sealed record RouterOsPoll(bool Confirm);
internal sealed record RouterOsSection(string Name,string Path,string[] Fields);
internal sealed record RouterOsRead(string Status,List<Dictionary<string,string>> Rows);

public static class RouterOsInventory
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher");
    private static IResult Denied()=>Results.Problem("RouterOS permissions required",statusCode:403);
    private static IResult Invalid(string message)=>Results.BadRequest(new{error=message});
    private static IResult Conflict(string message)=>Results.Conflict(new{error=message});
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    private static IDataProtector Protector(IDataProtectionProvider dp,Guid id)=>
        dp.CreateProtector("NetForge.RouterOS.Password.v1",id.ToString());

    // Do not request or store comments, scripts, PPP secrets, RADIUS secrets,
    // DHCP host names, serial numbers or arbitrary REST response fields.
    private static readonly RouterOsSection[] Sections=[
        new("resource","system/resource",["version","board-name","architecture-name","cpu-load","free-memory","total-memory","uptime"]),
        new("identity","system/identity",["name"]),
        new("routerboard","system/routerboard",["model","current-firmware","upgrade-firmware"]),
        new("interfaces","interface",["name","type","mtu","actual-mtu","running","disabled"]),
        new("vlans","interface/vlan",["name","interface","vlan-id","disabled"]),
        new("bridges","interface/bridge",["name","protocol-mode","vlan-filtering","disabled"]),
        new("addresses","ip/address",["address","network","interface","dynamic","disabled"]),
        new("routes","ip/route",["dst-address","gateway","routing-table","distance","active","dynamic","disabled"]),
        new("pppoe","interface/pppoe-server/server",["service-name","interface","default-profile","disabled"]),
        new("dhcp","ip/dhcp-server",["name","interface","address-pool","disabled"])
    ];
    private static bool Credentials(string? user,string? password,string? pin)=>
        user is not null && user.Length is >=1 and <=80 &&
        user.All(c=>c is >= '!' and <= '~' && c!=':') &&
        password is not null && password.Length is >=12 and <=256 &&
        password.All(c=>c is >= ' ' and <= '~') &&
        pin is not null && pin.Length==64 && pin.All(char.IsAsciiHexDigit);

    // Explicit server configuration, not a browser-controlled allowlist.
    // Deny loopback, link-local, multicast, unspecified and reserved IPv4.
    // No DNS resolution, proxy, redirect, IPv6 or cloud metadata address.
    private static bool Allowed(IConfiguration config,string? value,int port)
    {
        if(port is <1 or >65535 || !IPAddress.TryParse(value,out var ip) ||
            ip.AddressFamily!=AddressFamily.InterNetwork || ip.ToString()!=value)return false;
        var b=ip.GetAddressBytes();
        if(b[0] is 0 or 127 or >=224 || (b[0]==169&&b[1]==254) ||
            (b[0]==100&&b[1]==100&&b[2]==100&&b[3]==200))return false;
        var configured=(config["NETFORGE_ROUTEROS_ALLOWED_CIDRS"]??"")
            .Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        // A malformed entry fails the entire policy closed.
        var ranges=new List<IPNetwork>();
        foreach(var cidr in configured)
        {
            if(!IPNetwork.TryParse(cidr,out var range)||
               range.BaseAddress.AddressFamily!=AddressFamily.InterNetwork||
               range.PrefixLength<8)return false;
            ranges.Add(range);
        }
        return ranges.Any(r=>r.Contains(ip));
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/routeros/devices",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT id,nas_id,equipment_id,name,host(address) AS address,port,
                  username,certificate_sha256,enabled,last_poll_at,last_status,created_at
                FROM routeros_devices ORDER BY name,id LIMIT 2000
                """));
        });
        api.MapGet("/routeros/devices/{id:guid}",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            var rows=await Store.Rows(db,"""
                SELECT id,name,host(address) AS address,port,enabled,last_poll_at,last_status,
                  snapshot::text AS snapshot FROM routeros_devices WHERE id=@id
                """,P("id",id));
            ctx.Response.Headers.CacheControl="no-store";
            return rows.Count==0?Results.NotFound():Results.Ok(rows.Single());
        });
        api.MapPost("/routeros/devices",async(HttpContext ctx,NpgsqlDataSource db,
            IDataProtectionProvider dp,IConfiguration config,RouterOsEnroll input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=(input.Name??"").Trim();
            if(!input.Confirm || (input.NasId is null)==(input.EquipmentId is null) ||
               name.Length is <1 or >160 || !Credentials(input.Username,input.Password,input.CertificateSha256))
                return Invalid("Select one saved device, confirm enrollment, enter valid credentials and a SHA-256 certificate fingerprint");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var src=await NetworkManagement.Query(conn,tx,"""
                SELECT COALESCE((SELECT nasname FROM nas WHERE id=@nas),
                  (SELECT host(management_ip) FROM equipment WHERE id=@equipment)) AS address
                """,P("nas",input.NasId),P("equipment",input.EquipmentId));
            var address=src.Single()["address"] as string;
            if(!Allowed(config,address,input.Port))return Conflict("Saved address or port is outside the server's RouterOS access policy");
            var id=Guid.NewGuid();
            await NetworkManagement.Execute(conn,tx,"""
                INSERT INTO routeros_devices(id,nas_id,equipment_id,name,address,port,username,
                  password_ciphertext,certificate_sha256)
                VALUES(@id,@nas,@equipment,@name,@address::inet,@port,@user,@secret,@pin)
                """,P("id",id),P("nas",input.NasId),P("equipment",input.EquipmentId),
                P("name",name),P("address",address),P("port",input.Port),P("user",input.Username),
                P("secret",Protector(dp,id).Protect(input.Password)),P("pin",input.CertificateSha256.ToUpperInvariant()));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"routeros-enroll","routeros_device",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/routeros/devices/"+id,new{id,enabled=false});
        });
        api.MapPost("/routeros/devices/{id:guid}/credentials",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,IDataProtectionProvider dp,RouterOsCredentials input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm||!Credentials(input.Username,input.Password,input.CertificateSha256))
                return Invalid("Confirm valid replacement credentials and certificate fingerprint");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,tx,"""
                UPDATE routeros_devices SET username=@user,password_ciphertext=@secret,
                  certificate_sha256=@pin,enabled=false,last_poll_at=NULL,last_status='unknown',snapshot=NULL
                WHERE id=@id RETURNING id
                """,P("id",id),P("user",input.Username),P("secret",Protector(dp,id).Protect(input.Password)),
                P("pin",input.CertificateSha256.ToUpperInvariant()));
            if(rows.Count==0)return Results.NotFound();
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"routeros-rotate","routeros_device",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,enabled=false});
        });
        api.MapPost("/routeros/devices/{id:guid}/enabled",async(Guid id,HttpContext ctx,NpgsqlDataSource db,RouterOsEnabled input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Explicit confirmation required");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,tx,
                "UPDATE routeros_devices SET enabled=@enabled WHERE id=@id RETURNING id",
                P("id",id),P("enabled",input.Enabled));
            if(rows.Count==0)return Results.NotFound();
            await NetworkManagement.Audit(conn,tx,Actor(ctx),input.Enabled?"routeros-enable":"routeros-disable","routeros_device",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/routeros/devices/{id:guid}/poll",Poll).RequireRateLimiting("routeros-read");
    }

    private static readonly SemaphoreSlim PollSlots=new(4,4);
    private static async Task<IResult> Poll(Guid id,HttpContext ctx,NpgsqlDataSource db,
        IDataProtectionProvider dp,IConfiguration config,RouterOsPoll input)
    {
        if(!Admin(ctx))return Denied();
        if(!input.Confirm)return Invalid("Confirm read-only inspection of this enrolled router");
        return await PollCore(id,db,dp,config,Actor(ctx),ctx.RequestAborted,false);
    }
    internal static async Task<IResult> PollCore(Guid id,NpgsqlDataSource db,IDataProtectionProvider dp,
        IConfiguration config,string actor,CancellationToken token,bool scheduled)
    {
        if(!await PollSlots.WaitAsync(0,token))return Results.StatusCode(429);
        try{return await Inspect(id,db,dp,config,actor,token,scheduled);}
        finally{PollSlots.Release();}
    }
    private static async Task<IResult> Inspect(Guid id,NpgsqlDataSource db,IDataProtectionProvider dp,
        IConfiguration config,string actor,CancellationToken token,bool scheduled)
    {
        await using var conn=await db.OpenConnectionAsync(token);
        await using var tx=await conn.BeginTransactionAsync(token);
        // Hold one row lock for a bounded poll. Other polls fail fast instead
        // of queuing. Disable/rotate waits until the in-flight request finishes.
        var rows=await NetworkManagement.Query(conn,tx,"""
            SELECT d.enabled,host(d.address) AS address,d.port,d.username,d.password_ciphertext,
              d.certificate_sha256,d.last_poll_at,
              COALESCE(n.nasname,host(e.management_ip)) AS current_address
            FROM routeros_devices d LEFT JOIN nas n ON n.id=d.nas_id
              LEFT JOIN equipment e ON e.id=d.equipment_id
            WHERE d.id=@id FOR UPDATE OF d SKIP LOCKED
            """,P("id",id));
        if(rows.Count==0)return Conflict("Device not found or inspection already running");
        var row=rows.Single();
        if(scheduled)
        {
            // Advance due time only while holding the device lock. This
            // prevents competing workers from polling the same due item.
            var due=await NetworkManagement.Query(conn,tx,"""
                UPDATE routeros_compliance_policies SET next_poll_at=now()+make_interval(mins=>poll_interval_minutes)
                WHERE device_id=@id AND poll_interval_minutes>0 AND next_poll_at<=now()
                RETURNING device_id
                """,P("id",id));
            if(due.Count!=1)return Conflict("Inspection is not due");
        }
        var address=(string)row["address"]!;
        var port=(int)row["port"]!;
        if(row["enabled"] is not true || row["current_address"] as string!=address ||
            !Allowed(config,address,port))
        {
            // Do not leave a previously successful snapshot looking current
            // after policy denies access. No network request was sent.
            await NetworkManagement.Execute(conn,tx,
                "UPDATE routeros_devices SET last_status='failed',snapshot=NULL WHERE id=@id",P("id",id));
            await tx.CommitAsync();
            return Conflict("Device disabled, inventory address changed, or server allowlist denied access");
        }
        if(row["last_poll_at"] is DateTime previous && previous>DateTime.UtcNow.AddSeconds(-60))
        {
            if(scheduled)await tx.CommitAsync();
            return Results.Problem("Wait 60 seconds between inspection attempts",statusCode:429);
        }
        var result=new Dictionary<string,RouterOsRead>(StringComparer.Ordinal);
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var password=Protector(dp,id).Unprotect((string)row["password_ciphertext"]!);
            var pin=Convert.FromHexString(((string)row["certificate_sha256"]!).Trim());
            // Pin explicitly replaces CA/hostname trust, not TLS itself.
            // Expired/not-yet-valid certificates are rejected even if pinned.
            using var handler=new HttpClientHandler{
                AllowAutoRedirect=false,UseProxy=false,UseCookies=false,
                ServerCertificateCustomValidationCallback=(_,cert,_,_)=>
                    cert is not null && cert.NotBefore.ToUniversalTime()<=DateTime.UtcNow &&
                    cert.NotAfter.ToUniversalTime()>=DateTime.UtcNow &&
                    CryptographicOperations.FixedTimeEquals(pin,SHA256.HashData(cert.RawData))
            };
            using var client=new HttpClient(handler){BaseAddress=new Uri("https://"+address+":"+port+"/rest/"),
                Timeout=Timeout.InfiniteTimeSpan};
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes((string)row["username"]!+":"+password)));
            foreach(var section in Sections)
            {
                if(budget.IsCancellationRequested){result[section.Name]=new("not_attempted",[]);continue;}
                var read=await Read(client,section,budget.Token);
                result[section.Name]=read;
                // A failed resource request means there is no authenticated
                // inventory session. Do not hammer other endpoints.
                if(section.Name=="resource"&&read.Status!="ok")break;
            }
        }
        catch(CryptographicException){result["resource"]=new("credential_unavailable",[]);}
        foreach(var section in Sections)result.TryAdd(section.Name,new("not_attempted",[]));
        var successes=result.Values.Count(x=>x.Status=="ok");
        var status=successes==0?"failed":successes==Sections.Length?"ok":"partial";
        // Persist only allowlisted data and sanitized error codes. A failed
        // inspection replaces the current view; it is not a network outage.
        await NetworkManagement.Execute(conn,tx,"""
            UPDATE routeros_devices SET snapshot=@snapshot::jsonb,last_poll_at=now(),last_status=@status
            WHERE id=@id
            """,P("snapshot",JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            P("status",status),P("id",id));
        await NetworkManagement.Audit(conn,tx,actor,"routeros-inspect:"+status,"routeros_device",id.ToString());
        await tx.CommitAsync();
        return Results.Ok(new{status,sectionsOk=successes,totalSections=Sections.Length});
    }

    private static async Task<RouterOsRead> Read(HttpClient client,RouterOsSection section,CancellationToken budget)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(budget);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ct=timeout.Token;
        try
        {
            using var response=await client.GetAsync(section.Path+"?.proplist="+
                Uri.EscapeDataString(string.Join(",",section.Fields)),HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)return new("http_"+(int)response.StatusCode,[]);
            const int maxBytes=512*1024;
            if(response.Content.Headers.ContentLength>maxBytes)return new("too_large",[]);
            await using var stream=await response.Content.ReadAsStreamAsync(ct);
            using var bytes=new MemoryStream();
            var buffer=new byte[8192];
            while(true)
            {
                var n=await stream.ReadAsync(buffer.AsMemory(),ct);
                if(n==0)break;
                if(bytes.Length+n>maxBytes)return new("too_large",[]);
                bytes.Write(buffer,0,n);
            }
            using var json=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=8});
            if(json.RootElement.ValueKind!=JsonValueKind.Array)return new("invalid_json",[]);
            var output=new List<Dictionary<string,string>>();
            foreach(var item in json.RootElement.EnumerateArray())
            {
                if(output.Count>=1000)return new("too_many_rows",[]);
                if(item.ValueKind!=JsonValueKind.Object)return new("invalid_json",[]);
                var clean=new Dictionary<string,string>(StringComparer.Ordinal);
                foreach(var field in section.Fields)
                {
                    if(!item.TryGetProperty(field,out var value))continue;
                    if(value.ValueKind!=JsonValueKind.String)continue;
                    var str=value.GetString()??"";
                    clean[field]=str.Length<=512?str:str[..512];
                }
                output.Add(clean);
            }
            if(section.Name=="resource"&&(output.Count!=1||!output[0].ContainsKey("version")))
                return new("invalid_resource",[]);
            return new("ok",output);
        }
        catch(OperationCanceledException){return new("timeout",[]);}
        catch(HttpRequestException){return new("transport_or_tls",[]);}
        catch(IOException){return new("transport_or_tls",[]);}
        catch(JsonException){return new("invalid_json",[]);}
    }
}
