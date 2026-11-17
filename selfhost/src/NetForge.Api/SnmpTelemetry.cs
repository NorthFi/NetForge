// root/selfhost/src/NetForge.Api/SnmpTelemetry.cs
// SNMPv3 authPriv polling via Net-SNMP (snmpget), with DataProtection-encrypted
// secrets and per-request 0700 configuration directories. No SNMPv1/v2c.
// Passive IF-MIB counters; never alters NAS or radio configuration.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace NetForge.Api;

public sealed record SnmpDeviceInput(
    int? NasId,Guid? EquipmentId,Guid? HighsiteId,string Name,
    string Username,string AuthProtocol,string AuthPassword,string PrivacyPassword,
    int UdpPort,int InterfaceIndex,int IntervalSeconds,bool Enabled,bool Confirm);
public sealed record SnmpDeviceUpdate(
    bool Enabled,int IntervalSeconds,int InterfaceIndex,bool Confirm);
public sealed record SnmpCredentialChange(
    string Username,string AuthProtocol,string AuthPassword,string PrivacyPassword,
    bool Confirm);

internal sealed record SnmpReading(
    long UptimeTicks,int InterfaceStatus,decimal InputOctets,decimal OutputOctets);

public static class SnmpTelemetry
{
    private static readonly Regex UserPattern=new(
        @"^[a-zA-Z0-9_.@-]{1,80}$",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static readonly Regex SecretPattern=new(
        @"^[a-zA-Z0-9_.@!#%+=:/?~-]{12,128}$",
        RegexOptions.Compiled|RegexOptions.CultureInvariant);
    private static NpgsqlParameter P(string name,object? value)=>Store.P(name,value);
    private static bool Staff(HttpContext ctx)=>ctx.User.IsInRole("admin")||
        ctx.User.IsInRole("dispatcher");
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static IResult Denied()=>Results.Problem("SNMP administrator permissions required",statusCode:403);
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";

    private static IDataProtector Protector(IDataProtectionProvider provider)=>
        provider.CreateProtector("NetForge.SnmpV3.Credentials.v1");

    private static bool ValidCredentials(string? username,string? protocol,
        string? authPassword,string? privPassword)=>
        username is not null && UserPattern.IsMatch(username) &&
        protocol is "SHA" or "SHA-256" &&
        authPassword is not null && SecretPattern.IsMatch(authPassword) &&
        privPassword is not null && SecretPattern.IsMatch(privPassword);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/snmp/devices",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT d.id,d.equipment_id,d.nas_id,d.highsite_id,h.name AS highsite,
                    d.name,d.username,d.auth_protocol,d.udp_port,d.interface_index,
                    d.enabled,d.interval_seconds,d.last_polled_at,d.state,
                    d.failure_streak,d.last_error,d.created_at,
                    COALESCE(n.nasname,host(e.management_ip)) AS address
                FROM snmp_devices d
                LEFT JOIN equipment e ON e.id=d.equipment_id
                LEFT JOIN nas n ON n.id=d.nas_id
                LEFT JOIN highsites h ON h.id=d.highsite_id
                ORDER BY d.name LIMIT 2000
                """));
        });
        api.MapPost("/snmp/devices",async(HttpContext ctx,NpgsqlDataSource db,
            IDataProtectionProvider provider,SnmpDeviceInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if((input.NasId is null)==(input.EquipmentId is null) ||
               (input.Name??"").Trim().Length is < 1 or > 160 ||
               input.UdpPort is < 1 or > 65535 ||
               input.InterfaceIndex is < 1 ||
               input.IntervalSeconds is < 60 or > 3600 ||
               (input.Enabled&&!input.Confirm) ||
               !ValidCredentials(input.Username,input.AuthProtocol,input.AuthPassword,
                   input.PrivacyPassword))
                return Results.BadRequest(new{
                    error="Select an inventoried device, valid interface index and SNMPv3 authPriv credentials; confirm enabling"});
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var valid=await Source(conn,(NpgsqlTransaction)tx,input.NasId,
                input.EquipmentId,input.HighsiteId);
            if(valid is null)return Results.Conflict(new{
                error="Device must have a saved IPv4 address and a consistent highsite assignment"});
            var cipher=Protector(provider);
            var id=Guid.NewGuid();
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO snmp_devices(id,equipment_id,nas_id,highsite_id,
                    name,username,auth_protocol,auth_secret_ciphertext,
                    priv_secret_ciphertext,udp_port,interface_index,
                    interval_seconds,enabled)
                VALUES(@id,@equipment,@nas,@site,@name,@username,@protocol,
                    @auth,@privacy,@port,@interface,@interval,@enabled)
                """,P("id",id),P("equipment",input.EquipmentId),P("nas",input.NasId),
                P("site",input.HighsiteId),P("name",input.Name!.Trim()),
                P("username",input.Username),P("protocol",input.AuthProtocol),
                P("auth",cipher.Protect(input.AuthPassword)),
                P("privacy",cipher.Protect(input.PrivacyPassword)),
                P("port",input.UdpPort),P("interface",input.InterfaceIndex),
                P("interval",input.IntervalSeconds),P("enabled",input.Enabled));
            await NetworkManagement.Audit(conn,(NpgsqlTransaction)tx,Actor(ctx),
                "snmp-profile-create","snmp_device",id.ToString());
            await tx.CommitAsync();
            return Results.Created($"/api/snmp/devices/{id}",
                new{id,note="SNMPv3 credentials encrypted and omitted from API results"});
        });
        api.MapPut("/snmp/devices/{id:guid}",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,SnmpDeviceUpdate input)=>
        {
            if(!Admin(ctx))return Denied();
            if(input.IntervalSeconds is < 60 or > 3600 ||
               input.InterfaceIndex is < 1 ||
               (input.Enabled&&!input.Confirm))
                return Results.BadRequest(new{error="Invalid interval/interface index or missing enable confirmation"});
            var count=await Store.Execute(db,"""
                UPDATE snmp_devices SET enabled=@enabled,interval_seconds=@interval,
                    interface_index=@interface,next_poll_at=now(),
                    state=CASE WHEN interface_index!=@interface THEN 'unknown' ELSE state END,
                    failure_streak=CASE WHEN interface_index!=@interface THEN 0 ELSE failure_streak END
                WHERE id=@id
                """,P("id",id),P("enabled",input.Enabled),
                P("interval",input.IntervalSeconds),P("interface",input.InterfaceIndex));
            if(count==0)return Results.NotFound();
            await Store.Audit(db,Actor(ctx),"snmp-profile-update","snmp_device",id.ToString());
            return Results.Ok(new{ok=true});
        });
        api.MapPut("/snmp/devices/{id:guid}/credentials",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,IDataProtectionProvider provider,SnmpCredentialChange input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm || !ValidCredentials(input.Username,input.AuthProtocol,
                input.AuthPassword,input.PrivacyPassword))
                return Results.BadRequest(new{error="Confirm complete SNMPv3 credential rotation"});
            var cipher=Protector(provider);
            var count=await Store.Execute(db,"""
                UPDATE snmp_devices SET username=@username,auth_protocol=@protocol,
                  auth_secret_ciphertext=@auth,priv_secret_ciphertext=@privacy,
                  state='unknown',failure_streak=0,next_poll_at=now()
                WHERE id=@id
                """,P("id",id),P("username",input.Username),
                P("protocol",input.AuthProtocol),
                P("auth",cipher.Protect(input.AuthPassword)),
                P("privacy",cipher.Protect(input.PrivacyPassword)));
            if(count==0)return Results.NotFound();
            await Store.Audit(db,Actor(ctx),"snmp-credentials-rotate","snmp_device",id.ToString());
            return Results.Ok(new{ok=true,revokedOldCredentials=true});
        });
        api.MapPost("/snmp/devices/{id:guid}/poll",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,IDataProtectionProvider provider,
            IConfiguration config,CancellationToken token)=>
        {
            if(!Admin(ctx))return Denied();
            var result=await Poll(db,provider,config,id,token);
            return result is null?Results.NotFound():Results.Ok(result);
        });
        api.MapGet("/snmp/devices/{id:guid}/history",async(Guid id,HttpContext ctx,
            NpgsqlDataSource db,int? hours)=>
        {
            if(!Staff(ctx))return Denied();
            if(hours is not null && hours is < 1 or > 720)
                return Results.BadRequest(new{error="History hours must be 1–720"});
            var found=await Store.Rows(db,"SELECT id FROM snmp_devices WHERE id=@id",P("id",id));
            if(found.Count==0)return Results.NotFound();
            return Results.Ok(await Store.Rows(db,"""
                SELECT observed_at,uptime_ticks,oper_status,input_octets,output_octets,
                       input_bps,output_bps,counter_reset
                FROM (
                    SELECT id,observed_at,uptime_ticks,oper_status,input_octets,
                           output_octets,input_bps,output_bps,counter_reset
                    FROM snmp_samples
                    WHERE device_id=@id
                      AND observed_at>=now()-make_interval(hours=>@hours)
                    ORDER BY observed_at DESC,id DESC LIMIT 1000
                ) recent ORDER BY observed_at
                """,P("id",id),P("hours",hours??24)));
        });
        api.MapGet("/snmp/events",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT e.id,e.device_id,d.name,e.transition,e.reason,
                       e.suppressed,e.occurred_at
                FROM snmp_events e JOIN snmp_devices d ON d.id=e.device_id
                ORDER BY e.occurred_at DESC LIMIT 500
                """));
        });
    }

    private static async Task<string?> Source(NpgsqlConnection conn,NpgsqlTransaction tx,
        int? nasId,Guid? equipmentId,Guid? desiredSite)
    {
        var rows=await NetworkManagement.Query(conn,tx,"""
            SELECT COALESCE(n.nasname,host(e.management_ip)) AS address,
                   e.highsite_id AS equipment_site,c.highsite_id AS customer_site
            FROM (SELECT @nas::integer AS nas_id,@equipment::uuid AS equipment_id) input
            LEFT JOIN nas n ON n.id=input.nas_id
            LEFT JOIN equipment e ON e.id=input.equipment_id
            LEFT JOIN customers c ON c.id=e.customer_id
            """,P("nas",nasId),P("equipment",equipmentId));
        if(rows.Count==0)return null;
        var source=rows.Single();
        var address=source["address"] as string;
        if(!IPAddress.TryParse(address,out var ip) ||
           ip.AddressFamily!=AddressFamily.InterNetwork)return null;
        var known=source["equipment_site"] as Guid? ??
            source["customer_site"] as Guid?;
        if(known is not null && desiredSite!=known)return null;
        if(desiredSite is not null)
        {
            var exists=await NetworkManagement.Query(conn,tx,
                "SELECT id FROM highsites WHERE id=@id",P("id",desiredSite));
            if(exists.Count==0)return null;
        }
        return ip.ToString();
    }

    internal static decimal? Rate(decimal current,decimal previous,TimeSpan elapsed)
    {
        if(elapsed.TotalSeconds<=0 || current<previous)return null;
        var rate=(current-previous)*8m/(decimal)elapsed.TotalSeconds;
        // More than 1 Tbit/s on a single access interface is implausible in
        // this product tier and usually means a counter index/reset mismatch.
        if(rate>1_000_000_000_000m)return null;
        return Math.Round(rate,2,MidpointRounding.AwayFromZero);
    }

    internal static async Task<SnmpReading> QueryNetSnmp(string fileName,IPAddress ip,
        int port,int ifIndex,string username,string protocol,string auth,string privacy,
        CancellationToken cancellation)
    {
        var directory=Path.Combine(Path.GetTempPath(),
            "netforge-snmp-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if(!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory,UnixFileMode.UserRead|
                UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            var config=string.Join('\n',new[]{
                "defVersion 3","defSecurityLevel authPriv",
                "defSecurityName "+username,
                "defAuthType "+protocol,
                "defAuthPassphrase "+auth,
                "defPrivType AES",
                "defPrivPassphrase "+privacy,
                ""
            });
            var secretPath=Path.Combine(directory,"snmp.conf");
            var options=new FileStreamOptions{
                Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None
            };
            if(!OperatingSystem.IsWindows())
                options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            await using(var file=new FileStream(secretPath,options))
            {
                var bytes=Encoding.UTF8.GetBytes(config);
                await file.WriteAsync(bytes,cancellation);
                await file.FlushAsync(cancellation);
            }

            var psi=new ProcessStartInfo(fileName){
                UseShellExecute=false,RedirectStandardOutput=true,
                RedirectStandardError=true,CreateNoWindow=true,
                WorkingDirectory=directory
            };
            psi.Environment["SNMPCONFPATH"]=directory;
            psi.Environment["SNMP_PERSISTENT_DIR"]=directory;
            psi.Environment["HOME"]=directory;
            // Do not pass the -A/-X passwords on command lines or to the shell.
            foreach(var arg in new[]{
                "-v","3","-l","authPriv","-u",username,
                "-a",protocol,"-x","AES","-t","2","-r","0","-Oqv",
                "udp:"+ip+":"+port,
                ".1.3.6.1.2.1.1.3.0",
                ".1.3.6.1.2.1.2.2.1.8."+ifIndex,
                ".1.3.6.1.2.1.31.1.1.1.6."+ifIndex,
                ".1.3.6.1.2.1.31.1.1.1.10."+ifIndex
            })psi.ArgumentList.Add(arg);

            using var process=new Process{StartInfo=psi};
            try
            {
                if(!process.Start())throw new InvalidOperationException("SNMP client unavailable");
            }
            catch(System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException("SNMP client executable unavailable");
            }
            try
            {
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output=await stdout;
                _=await stderr; // intentionally do not expose provider diagnostics or secrets
                if(process.ExitCode!=0)
                    throw new InvalidOperationException("SNMPv3 request failed");
                var values=output.Split('\n',StringSplitOptions.TrimEntries|
                    StringSplitOptions.RemoveEmptyEntries);
                if(values.Length!=4)throw new InvalidOperationException("Malformed SNMP response");
                var ticks=ParsePositive(values[0]);
                var status=ParsePositive(values[1]);
                var input=ParseDecimal(values[2]);
                var outputBytes=ParseDecimal(values[3]);
                if(ticks>uint.MaxValue || status is < 1 or > 7 ||
                   input>ulong.MaxValue || outputBytes>ulong.MaxValue)
                    throw new InvalidOperationException("SNMP counter value outside supported range");
                return new SnmpReading(ticks,(int)status,input,outputBytes);
            }
            catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("SNMPv3 request timed out");
            }
            finally
            {
                if(!process.HasExited)
                {
                    process.Kill(entireProcessTree:true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        finally
        {
            // Credentials on disk are short-lived and never persisted in logs.
            try{Directory.Delete(directory,recursive:true);}catch(IOException){}
            catch(UnauthorizedAccessException){}
        }
    }

    private static long ParsePositive(string raw)
    {
        var value=raw.Trim();
        var match=Regex.Match(value,@"\(([0-9]+)\)",RegexOptions.CultureInvariant);
        if(match.Success)value=match.Groups[1].Value;
        if(!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var result)
            || result<0)
            throw new InvalidOperationException("Invalid SNMP integer value");
        return result;
    }

    private static decimal ParseDecimal(string raw)
    {
        var value=raw.Trim();
        if(!decimal.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var result)
            || result<0 || result!=decimal.Truncate(result))
            throw new InvalidOperationException("Invalid SNMP Counter64 value");
        return result;
    }

    public static async Task<object?> Poll(NpgsqlDataSource db,
        IDataProtectionProvider provider,IConfiguration config,Guid deviceId,
        CancellationToken cancellation)
    {
        await using var conn=await db.OpenConnectionAsync(cancellation);
        await using var tx=await conn.BeginTransactionAsync(cancellation);
        var rows=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
            SELECT d.id,d.name,d.username,d.auth_protocol,d.auth_secret_ciphertext,
                   d.priv_secret_ciphertext,d.udp_port,d.interface_index,
                   d.interval_seconds,d.enabled,d.failure_streak,d.state,d.highsite_id,
                   COALESCE(n.nasname,host(e.management_ip)) AS address
            FROM snmp_devices d
            LEFT JOIN equipment e ON e.id=d.equipment_id
            LEFT JOIN nas n ON n.id=d.nas_id
            WHERE d.id=@id FOR UPDATE OF d
            """,P("id",deviceId));
        if(rows.Count==0)return null;
        var row=rows.Single();
        if(row["enabled"] is not true)
            return new{ok=false,note="SNMP polling disabled",state=row["state"]};
        var source=row["address"] as string;
        if(!IPAddress.TryParse(source,out var ip) ||
           ip.AddressFamily!=AddressFamily.InterNetwork)
            return new{ok=false,note="Inventory address is not a valid IPv4",state=row["state"]};
        var oldState=(string)row["state"]!;
        var failures=(int)row["failure_streak"]!;
        SnmpReading? sample=null;
        string? error=null;
        try
        {
            var cipher=Protector(provider);
            var auth=cipher.Unprotect((string)row["auth_secret_ciphertext"]!);
            var privacy=cipher.Unprotect((string)row["priv_secret_ciphertext"]!);
            var clientPath=config["NETFORGE_SNMPGET_PATH"];
            if(string.IsNullOrWhiteSpace(clientPath))clientPath="/usr/bin/snmpget";
            sample=await QueryNetSnmp(clientPath,ip,(int)row["udp_port"]!,
                (int)row["interface_index"]!,(string)row["username"]!,
                (string)row["auth_protocol"]!,auth,privacy,cancellation);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
        catch(Exception)
        {
            // Deliberately don't log command stderr or DataProtection exception
            // content (either can disclose environment or transport secrets).
            error="SNMPv3 read failed, timed out or credentials/key unavailable";
        }

        var success=sample is not null && sample.InterfaceStatus==1;
        var nextFailures=success?0:failures+1;
        var next=success?"up":nextFailures>=3?"down":oldState;
        if(sample is not null)
        {
            var previous=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                SELECT observed_at,uptime_ticks,input_octets,output_octets
                FROM snmp_samples WHERE device_id=@id
                ORDER BY observed_at DESC,id DESC LIMIT 1
                """,P("id",deviceId));
            var now=DateTime.UtcNow;
            decimal? inbound=null,outbound=null;
            var reset=false;
            if(previous.Count>0)
            {
                var old=previous.Single();
                var elapsed=now-(DateTime)old["observed_at"]!;
                var oldInput=(decimal)old["input_octets"]!;
                var oldOutput=(decimal)old["output_octets"]!;
                reset=sample.UptimeTicks<=(long)old["uptime_ticks"]! ||
                    sample.InputOctets<oldInput || sample.OutputOctets<oldOutput;
                if(!reset)
                {
                    inbound=Rate(sample.InputOctets,oldInput,elapsed);
                    outbound=Rate(sample.OutputOctets,oldOutput,elapsed);
                    if(inbound is null || outbound is null)reset=true;
                }
            }
            await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO snmp_samples(device_id,observed_at,uptime_ticks,oper_status,
                    input_octets,output_octets,input_bps,output_bps,counter_reset)
                VALUES(@id,@observed,@ticks,@status,@input,@output,@input_bps,
                    @output_bps,@reset)
                """,P("id",deviceId),P("observed",now),P("ticks",sample.UptimeTicks),
                P("status",sample.InterfaceStatus),P("input",sample.InputOctets),
                P("output",sample.OutputOctets),P("input_bps",inbound),
                P("output_bps",outbound),P("reset",reset));
        }
        var reason=error??(success?"":"SNMP interface not operational");
        await NetworkManagement.Execute(conn,(NpgsqlTransaction)tx,"""
            UPDATE snmp_devices SET state=@state,failure_streak=@failures,
                last_polled_at=now(),last_error=@error,
                next_poll_at=now()+make_interval(secs=>@interval)
            WHERE id=@id
            """,P("id",deviceId),P("state",next),P("failures",nextFailures),
            P("error",reason),P("interval",(int)row["interval_seconds"]!));

        var transition=(next!=oldState && (next=="down"||oldState=="down"))
            ? next=="down"?"down":"recovered" : null;
        if(transition is not null)
        {
            var suppressed=await SiteImpact.IsSuppressed(conn,(NpgsqlTransaction)tx,
                row["highsite_id"] as Guid?);
            var eventRow=await NetworkManagement.Query(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO snmp_events(device_id,transition,reason,suppressed)
                VALUES(@id,@transition,@reason,@suppressed) RETURNING id
                """,P("id",deviceId),P("transition",transition),
                P("reason",success?"SNMPv3 interface reachable":reason),
                P("suppressed",suppressed));
            if(!suppressed)
            {
                var eventId=(Guid)eventRow.Single()["id"]!;
                await Notifications.EnqueueMonitorEvent(conn,(NpgsqlTransaction)tx,
                    eventId,"SNMPv3 "+row["name"]+" ("+ip+") "+
                    transition+". Interface index "+row["interface_index"]+
                    ". "+(error??"IF-MIB status change")+
                    ". This is device/interface evidence, not confirmed total highsite loss.",
                    config);
            }
        }
        await tx.CommitAsync(cancellation);
        return new{ok=true,polled=sample is not null,interfaceUp=success,
            state=next,failures=nextFailures,transition,note=reason};
    }

    public static async Task<int> PollDue(NpgsqlDataSource db,
        IDataProtectionProvider protector,IConfiguration config,
        ILogger logger,CancellationToken cancellation)
    {
        var due=await Store.Rows(db,"""
            SELECT id FROM snmp_devices WHERE enabled=true AND next_poll_at<=now()
            ORDER BY next_poll_at LIMIT 20
            """);
        var count=0;
        foreach(var row in due)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await Poll(db,protector,config,(Guid)row["id"]!,cancellation);
                count++;
            }
            catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
            catch(Exception e){logger.LogError(e,"SNMP telemetry collector failed for {DeviceId}",row["id"]);}
        }
        return count;
    }
}

public sealed class SnmpTelemetryWorker(NpgsqlDataSource database,
    IDataProtectionProvider provider,IConfiguration configuration,
    ILogger<SnmpTelemetryWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        while(!token.IsCancellationRequested)
        {
            try {await SnmpTelemetry.PollDue(database,provider,configuration,logger,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception error){logger.LogError(error,"SNMP telemetry worker encountered error");}
            try{if(!await timer.WaitForNextTickAsync(token))break;}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
        }
    }
}
