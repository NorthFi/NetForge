// root/selfhost/src/NetForge.Api/NetworkMonitoring.cs
// Authorized TCP service reachability: no ICMP, SNMP or arbitrary URL probing.
// Targets resolve exclusively from inventoried NAS/equipment IPv4 addresses.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace NetForge.Api;

public sealed record MonitorTargetInput(
    int? NasId,Guid? EquipmentId,string Name,int TcpPort,
    bool Enabled,int IntervalSeconds,int FailureThreshold,bool Confirm);
public sealed record MonitorTargetUpdate(
    bool Enabled,int IntervalSeconds,int FailureThreshold,bool Confirm);

public static class NetworkMonitoring
{
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static IResult Denied()=>Results.Problem("Administrator required",statusCode:403);
    private static bool Valid(int port,int interval,int threshold)=>
        port is >=1 and <=65535 && interval is >=60 and <=3600 &&
        threshold is >=2 and <=10;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/monitor/targets",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!ctx.User.IsInRole("admin")&&!ctx.User.IsInRole("dispatcher"))
                return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT t.id,t.name,t.nas_id,t.equipment_id,t.tcp_port,
                       t.enabled,t.interval_seconds,t.failure_threshold,
                       t.failure_streak,t.state,t.last_checked_at,
                       t.last_latency_ms,t.next_check_at,t.last_error,
                       t.highsite_id,t.is_upstream,
                       COALESCE(n.nasname,host(e.management_ip)) AS address
                FROM monitor_targets t
                LEFT JOIN nas n ON n.id=t.nas_id
                LEFT JOIN equipment e ON e.id=t.equipment_id
                ORDER BY t.name LIMIT 2000
                """));
        });
        api.MapPost("/monitor/targets",async(HttpContext ctx,NpgsqlDataSource db,
            MonitorTargetInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if((input.NasId is null)==(input.EquipmentId is null)||
               (input.Name??"").Trim().Length is <1 or >160 ||
               !Valid(input.TcpPort,input.IntervalSeconds,input.FailureThreshold) ||
               (input.Enabled&&!input.Confirm))
                return Results.BadRequest(new{error="Select exactly one saved NAS or equipment, valid probe options, and confirm enabling"});
            // Only IPv4 literals previously saved by an administrator are accepted:
            // no DNS lookup, URLs, redirect following or arbitrary user-supplied IP.
            var sources=await Store.Rows(db,"""
                SELECT COALESCE(
                    (SELECT nasname FROM nas WHERE id=@nas),
                    (SELECT host(management_ip) FROM equipment WHERE id=@equipment)
                ) AS address
                """,P("nas",input.NasId),P("equipment",input.EquipmentId));
            var value=sources.Single()["address"] as string;
            if(!IPAddress.TryParse(value,out var address) ||
               address.AddressFamily!=AddressFamily.InterNetwork)
                return Results.Conflict(new{error="Source must have a configured IPv4 address"});
            var id=await Store.Insert(db,"""
                INSERT INTO monitor_targets(nas_id,equipment_id,name,tcp_port,
                    enabled,interval_seconds,failure_threshold)
                VALUES(@nas,@equipment,@name,@port,@enabled,@interval,@threshold)
                RETURNING id
                """,P("nas",input.NasId),P("equipment",input.EquipmentId),
                P("name",input.Name!.Trim()),P("port",input.TcpPort),
                P("enabled",input.Enabled),P("interval",input.IntervalSeconds),
                P("threshold",input.FailureThreshold));
            await Store.Audit(db,ctx.User.Identity?.Name??"unknown",
                "monitor-add","monitor",id.ToString());
            return Results.Created($"/api/monitor/targets/{id}",new{id});
        });
        api.MapPut("/monitor/targets/{id:guid}",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,MonitorTargetUpdate input)=>
        {
            if(!Admin(ctx))return Denied();
            if(input.IntervalSeconds is <60 or >3600 ||
               input.FailureThreshold is <2 or >10 ||
               (input.Enabled&&!input.Confirm))
                return Results.BadRequest(new{error="Check interval 60–3600 seconds, threshold 2–10, confirmation required to enable"});
            var modified=await Store.Execute(db,"""
                UPDATE monitor_targets
                SET enabled=@enabled,interval_seconds=@interval,
                    failure_threshold=@threshold,next_check_at=now()
                WHERE id=@id
                """,P("id",id),P("enabled",input.Enabled),
                P("interval",input.IntervalSeconds),P("threshold",input.FailureThreshold));
            if(modified==0)return Results.NotFound();
            await Store.Audit(db,ctx.User.Identity?.Name??"unknown",
                "monitor-settings","monitor",id.ToString());
            return Results.Ok(new{ok=true});
        });
        api.MapPost("/monitor/targets/{id:guid}/check",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,IConfiguration configuration,CancellationToken token)=>
        {
            if(!Admin(ctx))return Denied();
            var result=await Check(db,configuration,id,token);
            return result is null?Results.NotFound():Results.Ok(result);
        });
        api.MapGet("/monitor/events",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!ctx.User.IsInRole("admin")&&!ctx.User.IsInRole("dispatcher"))
                return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT e.id,e.target_id,t.name,e.transition,e.suppressed,e.detail,e.occurred_at
                FROM monitor_events e JOIN monitor_targets t ON t.id=e.target_id
                ORDER BY e.occurred_at DESC LIMIT 500
                """));
        });
    }

    public static async Task<object?> Check(NpgsqlDataSource db,IConfiguration config,
        Guid targetId,CancellationToken cancellation,bool manual=true)
    {
        await using var conn=await db.OpenConnectionAsync(cancellation);
        await using var tx=await conn.BeginTransactionAsync(cancellation);
        // Row locking serializes worker and operator requests. The network check
        // has a bounded 2-second deadline to avoid long locks on down devices.
        var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            SELECT t.id,t.name,t.tcp_port,t.interval_seconds,t.failure_threshold,
                   t.failure_streak,t.state,t.enabled,t.highsite_id,t.is_upstream,t.next_check_at,
                   COALESCE(n.nasname,host(e.management_ip)) AS address
            FROM monitor_targets t
            LEFT JOIN nas n ON n.id=t.nas_id
            LEFT JOIN equipment e ON e.id=t.equipment_id
            WHERE t.id=@id FOR UPDATE OF t
            """,P("id",targetId));
        if(rows.Count==0)return null;
        var row=rows.Single();
        if(row["enabled"] is not true)
            return new{ok=false,state=row["state"],note="Target disabled"};
        if(!manual && row["next_check_at"] is DateTime next &&
           next>DateTime.UtcNow)
            return new{ok=false,state=row["state"],note="Another worker already checked this target"};
        var ipValue=row["address"] as string;
        if(!IPAddress.TryParse(ipValue,out var ip) ||
           ip.AddressFamily!=AddressFamily.InterNetwork)
            return new{ok=false,state=row["state"],note="No configured IPv4 address"};
        var port=(int)row["tcp_port"]!;
        var timer=Stopwatch.StartNew();
        var reachable=false;
        var error="";
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var tcp=new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(ip,port,timeout.Token);
            reachable=true;
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
        {
            error="TCP connect timed out";
        }
        catch(SocketException)
        {
            error="TCP connection failed";
        }
        timer.Stop();
        var latency=reachable?(int)Math.Min(timer.ElapsedMilliseconds,int.MaxValue): (int?)null;
        var streak=reachable?0:(int)row["failure_streak"]!+1;
        var before=(string)row["state"]!;
        var state=reachable?"up":
            streak>=(int)row["failure_threshold"]!?"down":before;
        var changed=state!=before && (state=="down" || before=="down");
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
            UPDATE monitor_targets
            SET failure_streak=@streak,state=@state,last_checked_at=now(),
                last_latency_ms=@latency,last_error=@error,
                next_check_at=now()+make_interval(secs=>@interval)
            WHERE id=@id
            """,P("id",targetId),P("streak",streak),P("state",state),
            P("latency",latency),P("error",error),
            P("interval",(int)row["interval_seconds"]!));
        if(changed)
        {
            var transition=state=="down"?"down":"recovered";
            var detail=state=="down"?"Consecutive TCP failures reached threshold":
                "TCP service reachable again";
            var events=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO monitor_events(target_id,transition,detail)
                VALUES(@target,@transition,@detail) RETURNING id
                """,P("target",targetId),P("transition",transition),
                P("detail",detail));
            var eventId=(Guid)events.Single()["id"]!;
            await SiteImpact.HandleTcpTransition(conn,(NpgsqlTransaction)tx,
                targetId,eventId,row["highsite_id"] as Guid?,
                row["is_upstream"] is true,transition,(string)row["name"]!,
                ip.ToString(),port,config);
        }
        await tx.CommitAsync(cancellation);
        return new{ok=true,state,reachable,failures=streak,latencyMs=latency,
            transition=changed?(state=="down"?"down":"recovered"):null,
            note="TCP port availability only; not ICMP, RF quality, SNMP or a full site health guarantee"};
    }

    public static async Task<int> CheckDue(NpgsqlDataSource db,IConfiguration config,
        ILogger logger,CancellationToken cancellation)
    {
        var due=await Store.Rows(db,"""
            SELECT id FROM monitor_targets
            WHERE enabled=true AND next_check_at<=now()
            ORDER BY next_check_at LIMIT 50
            """);
        var count=0;
        foreach(var item in due)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await Check(db,config,(Guid)item["id"]!,cancellation,manual:false);
                count++;
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
            catch(Exception error)
            {
                logger.LogError(error,"Network probe failed for target {Target}",item["id"]);
            }
        }
        return count;
    }
}

public sealed class MonitoringWorker(NpgsqlDataSource database,
    IConfiguration configuration,ILogger<MonitoringWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        while(!token.IsCancellationRequested)
        {
            try {await NetworkMonitoring.CheckDue(database,configuration,logger,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception e){logger.LogError(e,"Network monitor worker failed");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
}
