// root/selfhost/src/NetForge.Api/AssuranceCpeProbes.cs
// Independent management-path CPE TCP observations using inventoried assets;
// these are not customer-side or end-to-end Internet availability probes.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace NetForge.Api;

public sealed record CpeProbeInput(Guid ServiceId,Guid EquipmentId,int TcpPort,
    int IntervalSeconds,bool Enabled,bool Confirm);
public sealed record CpeProbeUpdate(int TcpPort,int IntervalSeconds,bool Enabled,bool Confirm);

public static class AssuranceCpeProbes
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Staff(HttpContext ctx)=>AssuranceAgreements.Staff(ctx);
    private static bool Admin(HttpContext ctx)=>AssuranceAgreements.Admin(ctx);
    private static IResult Denied()=>AssuranceAgreements.Denied();
    private static IResult Invalid(string message)=>AssuranceAgreements.Invalid(message);
    private static IResult Conflict(string message)=>AssuranceAgreements.Conflict(message);
    private static string Actor(HttpContext ctx)=>AssuranceAgreements.Actor(ctx);
    private static bool Allowed(int port)=>port is 22 or 80 or 443 or 8291 or 8729;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/assurance/cpe-probes",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT p.id,p.service_id,s.username,c.account,
                    p.equipment_id,e.name AS equipment,host(e.management_ip) AS address,
                    p.tcp_port,p.enabled,p.interval_seconds,p.state,
                    p.failure_streak,p.last_probed_at,p.last_latency_ms,
                    p.created_at
                FROM assurance_cpe_probes p
                JOIN services s ON s.id=p.service_id
                JOIN customers c ON c.id=s.customer_id
                JOIN equipment e ON e.id=p.equipment_id
                ORDER BY c.account,s.username LIMIT 3000
                """));
        });
        api.MapPost("/assurance/cpe-probes",async(HttpContext ctx,
            NpgsqlDataSource db,CpeProbeInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!Allowed(input.TcpPort) || input.IntervalSeconds is <60 or >3600 ||
               (input.Enabled&&!input.Confirm) ||
               input.ServiceId==Guid.Empty || input.EquipmentId==Guid.Empty)
                return Invalid("Select saved customer CPE, allowed management TCP port, interval 60–3600 and confirm enabling");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var valid=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT host(e.management_ip) AS address
                FROM services s JOIN equipment e ON e.customer_id=s.customer_id
                WHERE s.id=@service AND e.id=@equipment
                  AND e.category IN ('cpe','router')
                """,P("service",input.ServiceId),P("equipment",input.EquipmentId));
            if(valid.Count==0)return Conflict("Equipment must be a CPE/router asset assigned to this subscriber's customer account");
            if(!IPAddress.TryParse(valid.Single()["address"] as string,out var ip) ||
               ip.AddressFamily!=AddressFamily.InterNetwork)
                return Conflict("Inventory CPE must have an IPv4 literal management address");
            var existing=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,
                "SELECT id FROM assurance_cpe_probes WHERE service_id=@service",
                P("service",input.ServiceId));
            if(existing.Count>0)return Conflict("A CPE management probe already exists for that service");
            var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO assurance_cpe_probes(service_id,equipment_id,tcp_port,
                    interval_seconds,enabled)
                VALUES(@service,@equipment,@port,@interval,@enabled)
                RETURNING id
                """,P("service",input.ServiceId),P("equipment",input.EquipmentId),
                P("port",input.TcpPort),P("interval",input.IntervalSeconds),
                P("enabled",input.Enabled));
            var id=(Guid)rows.Single()["id"]!;
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "assurance-cpe-probe-create","assurance_probe",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/assurance/cpe-probes/"+id,
                new{id,enabled=input.Enabled,
                    note="Management-plane TCP reachability only; not an end-to-end subscriber internet test."});
        });
        api.MapPut("/assurance/cpe-probes/{id:guid}",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,CpeProbeUpdate input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!Allowed(input.TcpPort)||input.IntervalSeconds is <60 or >3600||
               (input.Enabled&&!input.Confirm))
                return Invalid("Allowed ports: 22,80,443,8291,8729; interval 60–3600 and explicit enable confirmation");
            var changes=await Store.Execute(db,"""
                UPDATE assurance_cpe_probes SET tcp_port=@port,interval_seconds=@interval,
                    enabled=@enabled,next_probe_at=now(),
                    state=CASE WHEN tcp_port<>@port THEN 'unknown' ELSE state END,
                    failure_streak=CASE WHEN tcp_port<>@port THEN 0 ELSE failure_streak END
                WHERE id=@id
                """,P("id",id),P("port",input.TcpPort),
                P("interval",input.IntervalSeconds),P("enabled",input.Enabled));
            if(changes==0)return Results.NotFound();
            await Store.Audit(db,Actor(ctx),"assurance-cpe-probe-update",
                "assurance_probe",id.ToString());
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/assurance/cpe-probes/{id:guid}/check",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,CancellationToken token)=>
        {
            if(!Admin(ctx))return Denied();
            var response=await Check(db,id,token);
            return response is null?Results.NotFound():Results.Ok(response);
        });
        api.MapGet("/assurance/cpe-probes/{id:guid}/history",async(Guid id,
            HttpContext ctx,NpgsqlDataSource db,int? hours)=>
        {
            if(!Staff(ctx))return Denied();
            if(hours is <1 or >720)
                return Invalid("Select 1–720 hours");
            var exists=await Store.Rows(db,
                "SELECT id FROM assurance_cpe_probes WHERE id=@id",P("id",id));
            if(exists.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT observed_at,reachable,latency_ms,state
                FROM (SELECT id,observed_at,reachable,latency_ms,state
                    FROM assurance_cpe_samples WHERE probe_id=@id
                      AND observed_at>=now()-make_interval(hours=>@hours)
                    ORDER BY observed_at DESC,id DESC LIMIT 1000
                ) recent ORDER BY observed_at
                """,P("id",id),P("hours",hours??24)));
        });
    }

    public static async Task<object?> Check(NpgsqlDataSource db,Guid id,
        CancellationToken cancellation,bool manual=true)
    {
        await using var conn=await db.OpenConnectionAsync(cancellation);
        await using var tx=await conn.BeginTransactionAsync(cancellation);
        var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            SELECT p.id,p.enabled,p.tcp_port,p.interval_seconds,
                p.next_probe_at,p.failure_streak,p.state,
                e.customer_id AS equipment_customer,
                s.customer_id AS service_customer,
                host(e.management_ip) AS address,e.category
            FROM assurance_cpe_probes p
            JOIN equipment e ON e.id=p.equipment_id
            JOIN services s ON s.id=p.service_id
            WHERE p.id=@id FOR UPDATE OF p
            """,P("id",id));
        if(rows.Count==0)return null;
        var row=rows.Single();
        if(row["enabled"] is not true)
            return new{ok=false,state=row["state"],note="CPE TCP polling disabled"};
        if(!manual && row["next_probe_at"] is DateTime due &&
           due>DateTime.UtcNow)
            return new{ok=false,state=row["state"],note="Another worker already checked the target"};
        // Cross-check the current inventory relationship every time, not only
        // on enrollment; account transfers must revoke access to that IP.
        if(!Equals(row["service_customer"],row["equipment_customer"]) ||
           (string)row["category"]! is not ("cpe" or "router") ||
           !IPAddress.TryParse(row["address"] as string,out var ip) ||
           ip.AddressFamily!=AddressFamily.InterNetwork)
        {
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,
                "UPDATE assurance_cpe_probes SET enabled=false,state='unknown',failure_streak=0 WHERE id=@id",
                P("id",id));
            await tx.CommitAsync(cancellation);
            return new{ok=false,state="unknown",note="Inventory owner/IP mismatch; probe automatically disabled"};
        }
        var timer=Stopwatch.StartNew();
        var reachable=false;
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var client=new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(ip,(int)row["tcp_port"]!,timeout.Token);
            reachable=true;
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested){}
        catch(SocketException){}
        timer.Stop();
        var delay=reachable?(int)Math.Min(timer.ElapsedMilliseconds,int.MaxValue):(int?)null;
        var streak=reachable?0:Math.Min(10000,(int)row["failure_streak"]!+1);
        var old=(string)row["state"]!;
        var state=reachable?"up":streak>=3?"down":old;
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
            UPDATE assurance_cpe_probes SET state=@state,failure_streak=@failures,
                last_latency_ms=@latency,last_probed_at=now(),
                next_probe_at=now()+make_interval(secs=>@interval) WHERE id=@id
            """,P("id",id),P("state",state),P("failures",streak),
            P("latency",delay),P("interval",(int)row["interval_seconds"]!));
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
            INSERT INTO assurance_cpe_samples(probe_id,reachable,latency_ms,state)
            VALUES(@id,@reachable,@latency,@state)
            """,P("id",id),P("reachable",reachable),P("latency",delay),
            P("state",state));
        await tx.CommitAsync(cancellation);
        return new{ok=true,reachable,state,latencyMs=delay,failures=streak,
            note="Server-to-CPE management port only; does not verify traffic forwarding, DNS or subscriber internet."};
    }

    public static async Task<int> CheckDue(NpgsqlDataSource db,
        ILogger logger,CancellationToken cancellation)
    {
        var rows=await Store.Rows(db,"""
            SELECT id FROM assurance_cpe_probes
            WHERE enabled=true AND next_probe_at<=now()
            ORDER BY next_probe_at LIMIT 20
            """);
        var count=0;
        foreach(var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await Check(db,(Guid)row["id"]!,cancellation,manual:false);
                count++;
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
            catch(Exception error){logger.LogError(error,
                "CPE management probe failed for {Probe}",row["id"]);}
        }
        return count;
    }
}

public sealed class AssuranceCpeProbeWorker(NpgsqlDataSource database,
    ILogger<AssuranceCpeProbeWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        while(!token.IsCancellationRequested)
        {
            try{await AssuranceCpeProbes.CheckDue(database,logger,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"CPE management probe worker failure");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
}
