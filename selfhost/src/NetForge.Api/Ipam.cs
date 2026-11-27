// root/selfhost/src/NetForge.Api/Ipam.cs
// Authoritative IPv4 static subscriber allocations, not DHCP or NAS dynamic
// pools. Never change an active session, invoke RouterOS, or release an address
// with credential/accounting history. A pool requires an operator-owned range.
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace NetForge.Api;

public sealed record IpamPoolInput(string Name,string Subnet,string? Gateway,string? Description,bool Confirm);
public sealed record IpamReservationInput(string Address,string Reason,bool Confirm);
public sealed record IpamAddressInput(string Address,bool Confirm);
public sealed record IpamAllocateInput(Guid ServiceId,string? Address,bool Confirm);
public sealed record IpamReleaseInput(Guid ServiceId,bool Confirm);

public static class Ipam
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher")||ctx.User.IsInRole("technician");
    private static IResult Denied()=>Results.Problem("Insufficient IPAM permissions",statusCode:403);
    private static IResult Invalid(string reason)=>Results.BadRequest(new{error=reason});
    private static IResult Conflict(string reason)=>Results.Conflict(new{error=reason});
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    private static Task Lock(NpgsqlConnection conn,NpgsqlTransaction tx)=>
        NetworkManagement.Execute(conn,tx,"SELECT pg_advisory_xact_lock(3100315)");

    // Reject shorthand, octal, hex, IPv4-mapped IPv6 and CIDR in host fields.
    private static bool Host(string? value,out uint number)
    {
        number=0;
        if(!IPAddress.TryParse(value,out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork
            ||ip.ToString()!=value)return false;
        number=BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        return true;
    }
    private static bool Pool(string? subnet,out uint first,out uint last)
    {
        first=last=0;
        var parts=(subnet??"").Split('/');
        if(parts.Length!=2||!Host(parts[0],out first)
            ||!int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var prefix)
            ||prefix is <20 or >30||parts[1]!=prefix.ToString(CultureInfo.InvariantCulture))return false;
        var mask=uint.MaxValue<<(32-prefix);
        last=first|~mask;
        // Reject loopback, unspecified, link-local, multicast and reserved
        // space. Private, CGNAT and operator-owned public ranges are allowed.
        var octet=first>>24;
        return (first&mask)==first && octet is >0 and <224 and not 127
            && (first&0xffff0000)!=0xa9fe0000;
    }
    private static async Task<bool> Exists(NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)=>
        (await NetworkManagement.Query(conn,tx,"SELECT id FROM ipam_pools WHERE id=@id",P("id",id))).Count==1;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/ipam/pools",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.id,p.name,p.subnet::text AS subnet,host(p.gateway) AS gateway,p.description,
                  set_masklen(broadcast(p.subnet::inet),32)-set_masklen(p.subnet::inet,32)-1 AS usable,
                  (SELECT count(*) FROM services s WHERE s.static_ipv4 <<= p.subnet) AS assigned,
                  (SELECT count(*) FROM ipam_reservations r WHERE r.pool_id=p.id) AS reserved
                FROM ipam_pools p ORDER BY p.subnet
                """));
        });
        api.MapGet("/ipam/pools/{id:guid}/addresses",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            if((await Store.Rows(db,"SELECT id FROM ipam_pools WHERE id=@id",P("id",id))).Count==0)
                return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT host(address) AS address,service_id,username,account,
                  service_state,reason,observed,radius_configured,state
                FROM ipam_inventory WHERE pool_id=@id ORDER BY address
                """,P("id",id)));
        });
        api.MapPost("/ipam/pools",async(HttpContext ctx,NpgsqlDataSource db,IpamPoolInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=(input.Name??"").Trim();
            var desc=(input.Description??"").Trim();
            var gateway=string.IsNullOrWhiteSpace(input.Gateway)?null:input.Gateway;
            if(!input.Confirm||name.Length is <1 or >100||desc.Length>240
                ||!Pool(input.Subnet,out var first,out var last)
                ||(gateway is not null&&(!Host(gateway,out var gw)||gw<=first||gw>=last)))
                return Invalid("Confirm a dedicated, canonical /20–/30 IPv4 subnet, optional usable gateway, name and description");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await Lock(conn,tx);
            var collisions=await NetworkManagement.Query(conn,tx,"""
                SELECT id FROM services WHERE static_ipv4 IN (
                  set_masklen(@subnet::cidr::inet,32),
                  set_masklen(broadcast(@subnet::cidr::inet),32),@gateway::inet)
                LIMIT 1
                """,P("subnet",input.Subnet),P("gateway",gateway));
            if(collisions.Count!=0)return Conflict("A subscriber already uses the network, broadcast or gateway address");
            var overlap=await NetworkManagement.Query(conn,tx,
                "SELECT id FROM ipam_pools WHERE subnet && @subnet::cidr LIMIT 1",P("subnet",input.Subnet));
            if(overlap.Count!=0)return Conflict("Overlapping IPAM subnet");
            var rows=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO ipam_pools(name,subnet,gateway,description)
                VALUES(@name,@subnet::cidr,@gateway::inet,@desc) RETURNING id
                """,P("name",name),P("subnet",input.Subnet),P("gateway",gateway),P("desc",desc));
            var id=(Guid)rows.Single()["id"]!;
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"ipam-pool-create","ipam_pool",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/ipam/pools/"+id,new{id});
        });
        api.MapPost("/ipam/pools/{id:guid}/reserve",async(Guid id,HttpContext ctx,NpgsqlDataSource db,IpamReservationInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var reason=(input.Reason??"").Trim();
            if(!input.Confirm||!Host(input.Address,out _)||reason.Length is <3 or >240)
                return Invalid("Confirm a canonical IPv4 host and reservation reason (3–240 characters)");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await Lock(conn,tx);
            if(!await Exists(conn,tx,id))return Results.NotFound();
            var rows=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO ipam_reservations(address,pool_id,reason)
                SELECT address,pool_id,@reason FROM ipam_inventory
                  WHERE pool_id=@id AND address=@ip::inet AND state='free'
                RETURNING address::text
                """,P("id",id),P("ip",input.Address),P("reason",reason));
            if(rows.Count==0)return Conflict("Address is not free in this pool");
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"ipam-reserve","ipam_address",input.Address);
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/ipam/pools/{id:guid}/unreserve",async(Guid id,HttpContext ctx,NpgsqlDataSource db,IpamAddressInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm||!Host(input.Address,out _))return Invalid("Confirm a canonical IPv4 host");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await Lock(conn,tx);
            var rows=await NetworkManagement.Query(conn,tx,"""
                DELETE FROM ipam_reservations WHERE pool_id=@id AND address=@ip::inet RETURNING address::text
                """,P("id",id),P("ip",input.Address));
            if(rows.Count==0)return Results.NotFound();
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"ipam-unreserve","ipam_address",input.Address);
            await tx.CommitAsync();
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/ipam/pools/{id:guid}/allocate",async(Guid id,HttpContext ctx,NpgsqlDataSource db,IpamAllocateInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var requested=string.IsNullOrWhiteSpace(input.Address)?null:input.Address;
            if(!input.Confirm||input.ServiceId==Guid.Empty||(requested is not null&&!Host(requested,out _)))
                return Invalid("Confirm a pending service and optional canonical IPv4 host");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await Lock(conn,tx);
            if(!await Exists(conn,tx,id))return Results.NotFound();
            var service=await Eligible(conn,tx,input.ServiceId);
            if(service.Count==0)return Conflict("Only pending services without RADIUS or accounting history are eligible");
            if(service[0]["ip"] is not null)return Conflict("Service already has a static address");
            var rows=await NetworkManagement.Query(conn,tx,"""
                SELECT host(address) AS ip FROM ipam_inventory
                WHERE pool_id=@id AND state='free'
                  AND (@ip::inet IS NULL OR address=@ip::inet)
                ORDER BY address LIMIT 1
                """,P("id",id),P("ip",requested));
            if(rows.Count==0)return Conflict("Requested address unavailable or pool exhausted");
            var address=(string)rows.Single()["ip"]!;
            await NetworkManagement.Execute(conn,tx,
                "UPDATE services SET static_ipv4=@ip::inet,changed_at=now() WHERE id=@id",
                P("ip",address),P("id",input.ServiceId));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"ipam-allocate:"+address,"service",input.ServiceId.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{address,note="Reserved for this service. Set PPPoE credentials to provision Framed-IP-Address through RADIUS."});
        });
        api.MapPost("/ipam/pools/{id:guid}/release",async(Guid id,HttpContext ctx,NpgsqlDataSource db,IpamReleaseInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm||input.ServiceId==Guid.Empty)return Invalid("Confirm the pending service");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await Lock(conn,tx);
            var service=await Eligible(conn,tx,input.ServiceId);
            if(service.Count==0||service[0]["ip"] is not string address)
                return Conflict("Only never-provisioned pending addresses can be released");
            var belongs=await NetworkManagement.Query(conn,tx,"""
                SELECT id FROM ipam_pools WHERE id=@id AND @ip::inet <<= subnet
                """,P("id",id),P("ip",address));
            if(belongs.Count==0)return Conflict("Service address is not in this pool");
            await NetworkManagement.Execute(conn,tx,
                "UPDATE services SET static_ipv4=NULL,changed_at=now() WHERE id=@id",P("id",input.ServiceId));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"ipam-release:"+address,"service",input.ServiceId.ToString());
            await tx.CommitAsync();
            return Results.Ok(new{ok=true,address});
        });
    }

    private static Task<List<Dictionary<string,object?>>> Eligible(NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)=>
        NetworkManagement.Query(conn,tx,"""
            SELECT host(s.static_ipv4) AS ip FROM services s
            WHERE s.id=@id AND s.state='pending'
              AND NOT EXISTS(SELECT 1 FROM radcheck x WHERE x.username=s.username)
              AND NOT EXISTS(SELECT 1 FROM radreply x WHERE x.username=s.username)
              AND NOT EXISTS(SELECT 1 FROM radacct x WHERE x.username=s.username)
            FOR UPDATE OF s
            """,P("id",id));
}
