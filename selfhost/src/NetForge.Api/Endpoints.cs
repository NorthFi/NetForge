// root/selfhost/src/NetForge.Api/Endpoints.cs
using System.Globalization;
using System.Net;
using System.Security.Claims;
using Npgsql;

namespace NetForge.Api;

public static class Endpoints
{
    private static bool CanEdit(HttpContext ctx) => Role(ctx, "admin", "dispatcher", "technician");
    private static bool IsAdmin(HttpContext ctx) => Role(ctx, "admin");
    private static bool Role(HttpContext ctx, params string[] roles) =>
        roles.Any(ctx.User.IsInRole);
    private static string Actor(HttpContext ctx) => ctx.User.Identity?.Name ?? "unknown";
    private static bool Allowed(string? value, params string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal);
    private static IResult Forbidden() => Results.Problem("Insufficient role permissions", statusCode:403);
    private static IResult Invalid(string message) => Results.BadRequest(new { error = message });
    private static object? Value(Dictionary<string,object?> row, string key) => row.GetValueOrDefault(key);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/dashboard", async (NpgsqlDataSource db) =>
        {
            var stats = await Store.Rows(db, """
                SELECT (SELECT count(*) FROM customers) AS customers,
                       (SELECT count(*) FROM highsites) AS highsites,
                       (SELECT count(*) FROM sectors) AS sectors,
                       (SELECT count(*) FROM services WHERE state='active') AS active_services,
                       (SELECT count(*) FROM tickets WHERE status NOT IN ('closed','resolved')) AS open_tickets,
                       (SELECT count(*) FROM radacct WHERE acctstoptime IS NULL) AS unclosed_radius_sessions,
                       (SELECT count(*) FROM invoices WHERE status IN ('issued','draft')) AS invoices,
                       (SELECT count(*) FROM nas) AS nas_devices
                """);
            return Results.Ok(stats.Single());
        });

        // Branches and staff can be managed by an administrator. Other users get
        // read access for navigation but cannot create staff credentials.
        api.MapGet("/branches", async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db, "SELECT id,name,address FROM branches ORDER BY name")));
        api.MapPost("/branches", async (HttpContext ctx, NpgsqlDataSource db, BranchInput body) =>
        {
            if (!IsAdmin(ctx)) return Forbidden();
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 160) return Invalid("Branch name is required");
            var id = await Store.Insert(db,
                "INSERT INTO branches(name,address) VALUES (@n,@a) RETURNING id",
                Store.P("n",body.Name.Trim()),Store.P("a",body.Address?.Trim()??""));
            await Store.Audit(db, Actor(ctx), "create", "branch", id.ToString());
            return Results.Created($"/api/branches/{id}",new { id });
        });
        api.MapGet("/staff", async (HttpContext ctx, NpgsqlDataSource db) =>
        {
            if (!IsAdmin(ctx)) return Forbidden();
            return Results.Ok(await Store.Rows(db,"SELECT id,username,role,enabled,created_at FROM app_users ORDER BY username"));
        });
        api.MapPost("/staff", async (HttpContext ctx, NpgsqlDataSource db, StaffInput body) =>
        {
            if (!IsAdmin(ctx)) return Forbidden();
            if (string.IsNullOrWhiteSpace(body.Username) || body.Username.Length > 80 ||
                body.Password?.Length < 14 || !Allowed(body.Role,"admin","dispatcher","technician","viewer"))
                return Invalid("Username, strong password and recognized role required");
            var id=await Store.Insert(db,
                "INSERT INTO app_users(username,password_hash,role) VALUES (@u,@p,@r) RETURNING id",
                Store.P("u",body.Username.Trim()),Store.P("p",Passwords.Hash(body.Password!)),
                Store.P("r",body.Role));
            await Store.Audit(db,Actor(ctx),"create","staff",id.ToString());
            return Results.Created($"/api/staff/{id}",new {id});
        });

        api.MapGet("/highsites", async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db,"""
                SELECT h.id,h.number,h.name,h.address,h.branch_id,b.name AS branch,
                       h.latitude,h.longitude,
                       (SELECT count(*) FROM customers c WHERE c.highsite_id=h.id) AS subscribers
                FROM highsites h LEFT JOIN branches b ON b.id=h.branch_id
                ORDER BY h.name
                """)));
        api.MapPost("/highsites", async (HttpContext ctx, NpgsqlDataSource db, SiteInput body) =>
        {
            if (!CanEdit(ctx)) return Forbidden();
            if (string.IsNullOrWhiteSpace(body.Name)||string.IsNullOrWhiteSpace(body.Number)||
                body.Name.Length>160||body.Number.Length>80||
                body.Latitude is < -90 or > 90 || body.Longitude is < -180 or > 180)
                return Invalid("Valid highsite number, name, and GPS coordinates are required");
            var id=await Store.Insert(db,"""
                INSERT INTO highsites(number,name,address,branch_id,latitude,longitude)
                VALUES (@num,@n,@a,@b,@lat,@lon) RETURNING id
                """,Store.P("num",body.Number.Trim()),Store.P("n",body.Name.Trim()),
                Store.P("a",body.Address?.Trim()??""),Store.P("b",body.BranchId),
                Store.P("lat",body.Latitude),Store.P("lon",body.Longitude));
            await Store.Audit(db,Actor(ctx),"create","highsite",id.ToString());
            return Results.Created($"/api/highsites/{id}",new {id});
        });

        api.MapGet("/sectors", async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db,"""
                SELECT s.id,s.highsite_id,h.name AS highsite,s.name,s.ssid,
                       s.azimuth,s.frequency_mhz,s.max_customers,
                       (SELECT count(*) FROM customers c WHERE c.sector_id=s.id) AS customers
                FROM sectors s JOIN highsites h ON h.id=s.highsite_id ORDER BY h.name,s.name
                """)));
        api.MapPost("/sectors", async (HttpContext ctx, NpgsqlDataSource db, SectorInput body) =>
        {
            if (!CanEdit(ctx)) return Forbidden();
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length>160 ||
                body.Azimuth is < 0 or >= 360 || body.MaxCustomers < 0)
                return Invalid("Sector name or RF parameters are invalid");
            var id=await Store.Insert(db,"""
                INSERT INTO sectors(highsite_id,name,ssid,azimuth,frequency_mhz,max_customers)
                VALUES (@h,@n,@ssid,@az,@mhz,@cap) RETURNING id
                """,Store.P("h",body.HighsiteId),Store.P("n",body.Name.Trim()),
                Store.P("ssid",body.Ssid?.Trim()??""),Store.P("az",body.Azimuth),
                Store.P("mhz",body.FrequencyMhz),Store.P("cap",body.MaxCustomers));
            await Store.Audit(db,Actor(ctx),"create","sector",id.ToString());
            return Results.Created($"/api/sectors/{id}",new {id});
        });

        api.MapGet("/customers", async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db,"""
                SELECT c.*,h.name AS highsite,s.name AS sector,b.name AS branch
                FROM customers c LEFT JOIN highsites h ON h.id=c.highsite_id
                LEFT JOIN sectors s ON s.id=c.sector_id
                LEFT JOIN branches b ON b.id=c.branch_id
                ORDER BY c.account LIMIT 10000
                """)));
        api.MapGet("/customers/{id:guid}", async (Guid id, NpgsqlDataSource db) =>
        {
            var result=await Store.Rows(db,"SELECT * FROM customers WHERE id=@id",Store.P("id",id));
            return result.Count==0?Results.NotFound():Results.Ok(result[0]);
        });
        api.MapPost("/customers", async (HttpContext ctx,NpgsqlDataSource db,CustomerInput body) =>
        {
            if (!CanEdit(ctx)) return Forbidden();
            if (!ValidCustomer(body)) return Invalid("Invalid account, customer name, status or highsite/sector association");
            var id=await Store.Insert(db,"""
                INSERT INTO customers(account,first_name,surname,email,phone,address,status,branch_id,highsite_id,sector_id)
                VALUES (@a,@n,@s,@e,@p,@addr,@st,@b,@h,@sector) RETURNING id
                """,CustomerParameters(body));
            await Store.Audit(db,Actor(ctx),"create","customer",id.ToString());
            return Results.Created($"/api/customers/{id}",new {id});
        });
        api.MapPut("/customers/{id:guid}",async (Guid id,HttpContext ctx,NpgsqlDataSource db,CustomerInput body) =>
        {
            if (!CanEdit(ctx)) return Forbidden();
            if (!ValidCustomer(body)) return Invalid("Invalid customer or site/sector association");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            await using var cmd=new NpgsqlCommand("""
                UPDATE customers SET account=@a,first_name=@n,surname=@s,email=@e,phone=@p,
                       address=@addr,status=@st,branch_id=@b,highsite_id=@h,sector_id=@sector,updated_at=now()
                WHERE id=@id
                """,conn,(NpgsqlTransaction)tx);
            cmd.Parameters.AddRange(CustomerParameters(body));
            cmd.Parameters.Add(Store.P("id",id));
            var n=await cmd.ExecuteNonQueryAsync();
            if (n==0) return Results.NotFound();
            if (body.Status is "suspended" or "cancelled")
            {
                // Revoking check items disables *new* authentication; an already
                // established PPPoE session needs a separate NAS CoA/Disconnect.
                await using var revoke=new NpgsqlCommand("""
                    DELETE FROM radcheck WHERE attribute='Cleartext-Password' AND
                    username IN (SELECT username FROM services WHERE customer_id=@id)
                    """,conn,(NpgsqlTransaction)tx);
                revoke.Parameters.AddWithValue("id",id);
                await revoke.ExecuteNonQueryAsync();
                await using var services=new NpgsqlCommand("""
                    UPDATE services SET state='suspended',changed_at=now()
                    WHERE customer_id=@id AND state='active'
                    """,conn,(NpgsqlTransaction)tx);
                services.Parameters.AddWithValue("id",id);
                await services.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            await Store.Audit(db,Actor(ctx),"update","customer",id.ToString());
            return Results.Ok(new {id,important="Active sessions are not disconnected by this change"});
        });

        api.MapGet("/equipment",async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db,"""
                SELECT e.id,e.customer_id,c.account,e.highsite_id,h.name AS highsite,
                       e.category,e.name,e.model,e.management_ip::text AS management_ip,
                       e.serial_number,e.ownership,e.created_at
                FROM equipment e LEFT JOIN customers c ON c.id=e.customer_id
                LEFT JOIN highsites h ON h.id=e.highsite_id ORDER BY e.created_at DESC
                LIMIT 10000
                """)));
        api.MapPost("/equipment",async (HttpContext ctx,NpgsqlDataSource db,EquipmentInput body) =>
        {
            if(!CanEdit(ctx))return Forbidden();
            if((body.CustomerId is null)==(body.HighsiteId is null)||
                string.IsNullOrWhiteSpace(body.Name)||body.Name.Length>160||
                !Allowed(body.Category,"distribution","backhaul","access","power","cpe","router","other"))
                return Invalid("Equipment must belong to exactly one customer or highsite, with a valid category and name");
            if(!string.IsNullOrWhiteSpace(body.ManagementIp)&&!IPAddress.TryParse(body.ManagementIp,out _))
                return Invalid("Management IP must be a valid IP address");
            var id=await Store.Insert(db,"""
                INSERT INTO equipment(customer_id,highsite_id,category,name,model,management_ip,serial_number,ownership)
                VALUES (@c,@h,@cat,@n,@model,CAST(@ip AS inet),@serial,@owned) RETURNING id
                """,Store.P("c",body.CustomerId),Store.P("h",body.HighsiteId),
                Store.P("cat",body.Category),Store.P("n",body.Name.Trim()),
                Store.P("model",body.Model??""),Store.P("ip",string.IsNullOrWhiteSpace(body.ManagementIp)?null:body.ManagementIp),
                Store.P("serial",body.SerialNumber??""),Store.P("owned",body.Ownership??""));
            await Store.Audit(db,Actor(ctx),"create","equipment",id.ToString());
            return Results.Created($"/api/equipment/{id}",new{id});
        });

        api.MapGet("/packages",async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db,"SELECT * FROM packages ORDER BY name")));
        api.MapPost("/packages",async (HttpContext ctx,NpgsqlDataSource db,PackageInput body) =>
        {
            if (!IsAdmin(ctx)) return Forbidden();
            if (string.IsNullOrWhiteSpace(body.Name)||body.Name.Length>160||
                body.DownloadMbps<1||body.UploadMbps<1||body.MonthlyPrice<0||
                (body.MikrotikRateLimit?.Length??0)>64)
                return Invalid("Invalid package name, speeds, price or rate limit");
            var id=await Store.Insert(db,"""
                INSERT INTO packages(name,download_mbps,upload_mbps,monthly_price,mikrotik_rate_limit)
                VALUES (@n,@dl,@ul,@price,@rate) RETURNING id
                """,Store.P("n",body.Name.Trim()),Store.P("dl",body.DownloadMbps),
                Store.P("ul",body.UploadMbps),Store.P("price",body.MonthlyPrice),
                Store.P("rate",body.MikrotikRateLimit?.Trim()??""));
            await Store.Audit(db,Actor(ctx),"create","package",id.ToString());
            return Results.Created($"/api/packages/{id}",new {id});
        });

        api.MapGet("/services",async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db,"""
                SELECT s.id,s.customer_id,c.account,s.package_id,p.name AS package,
                       s.username,s.state,s.static_ipv4::text AS static_ipv4,
                       s.created_at,s.changed_at,
                       EXISTS(SELECT 1 FROM radcheck r WHERE r.username=s.username AND r.attribute='Cleartext-Password') AS radius_enabled
                FROM services s JOIN customers c ON c.id=s.customer_id
                JOIN packages p ON p.id=s.package_id ORDER BY c.account
                """)));
        api.MapPost("/services",async (HttpContext ctx,NpgsqlDataSource db,ServiceInput body) =>
        {
            if (!CanEdit(ctx)) return Forbidden();
            if (!ValidService(body)) return Invalid("Invalid PPPoE username, status or IPv4 address");
            // A service cannot become active until explicit RADIUS credential provisioning.
            if(body.State=="active")return Invalid("Create a pending service, then activate it using RADIUS credentials");
            var id=await Store.Insert(db,"""
                INSERT INTO services(customer_id,package_id,username,state,static_ipv4)
                VALUES(@c,@p,@u,@s,@ip::inet) RETURNING id
                """,Store.P("c",body.CustomerId),Store.P("p",body.PackageId),
                Store.P("u",body.Username.Trim()),Store.P("s",body.State),
                Store.P("ip",body.StaticIpv4));
            await Store.Audit(db,Actor(ctx),"create","service",id.ToString());
            return Results.Created($"/api/services/{id}",new {id});
        });
        api.MapPost("/services/{id:guid}/credentials",
            async (Guid id,HttpContext ctx,NpgsqlDataSource db,CredentialInput body) =>
        {
            if (!IsAdmin(ctx)) return Forbidden();
            if (string.IsNullOrEmpty(body.Password)||body.Password.Length<12||body.Password.Length>128)
                return Invalid("Subscriber PPPoE password must be 12–128 characters");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await InTransaction(conn,(NpgsqlTransaction)tx,"""
                SELECT s.username,s.state,s.static_ipv4::text AS ip,c.status AS customer_status,
                       p.mikrotik_rate_limit AS rate
                FROM services s JOIN customers c ON c.id=s.customer_id
                JOIN packages p ON p.id=s.package_id WHERE s.id=@id FOR UPDATE OF s
                """,Store.P("id",id));
            if(rows.Count==0)return Results.NotFound();
            var service=rows[0];
            if((Value(service,"customer_status") as string)!="active")
                return Invalid("Customer must be active before provisioning RADIUS");
            if((Value(service,"state") as string)=="cancelled")
                return Invalid("Cancelled services cannot be reactivated");
            var username=(string)Value(service,"username")!;
            await ExecTx(conn,(NpgsqlTransaction)tx,
                "DELETE FROM radcheck WHERE username=@u AND attribute='Cleartext-Password'",
                Store.P("u",username));
            await ExecTx(conn,(NpgsqlTransaction)tx,
                "DELETE FROM radreply WHERE username=@u AND attribute IN ('Mikrotik-Rate-Limit','Framed-IP-Address')",
                Store.P("u",username));
            // FreeRADIUS CHAP/MSCHAP requires a verifiable password.
            // Only the isolated RADIUS DB should read this Cleartext-Password row.
            await ExecTx(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO radcheck(username,attribute,op,value)
                VALUES (@u,'Cleartext-Password',':=',@pw)
                """,Store.P("u",username),Store.P("pw",body.Password));
            if(Value(service,"rate") is string rate && rate.Length>0)
                await ExecTx(conn,(NpgsqlTransaction)tx,"""
                    INSERT INTO radreply(username,attribute,op,value)
                    VALUES (@u,'Mikrotik-Rate-Limit','=',@rate)
                    """,Store.P("u",username),Store.P("rate",rate));
            if(Value(service,"ip") is string ip && ip.Length>0)
                await ExecTx(conn,(NpgsqlTransaction)tx,"""
                    INSERT INTO radreply(username,attribute,op,value)
                    VALUES (@u,'Framed-IP-Address','=',@ip)
                    """,Store.P("u",username),Store.P("ip",ip));
            await ExecTx(conn,(NpgsqlTransaction)tx,
                "UPDATE services SET state='active',changed_at=now() WHERE id=@id",Store.P("id",id));
            await AuditTx(conn,(NpgsqlTransaction)tx,Actor(ctx),"credentials-set","service",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new {ok=true,username,
                note="New PPPoE authentications use these credentials. Existing sessions are not disconnected."});
        });
        api.MapPost("/services/{id:guid}/suspend",async (Guid id,HttpContext ctx,NpgsqlDataSource db) =>
        {
            if (!IsAdmin(ctx))return Forbidden();
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var row=await InTransaction(conn,(NpgsqlTransaction)tx,
                "SELECT username FROM services WHERE id=@id FOR UPDATE",Store.P("id",id));
            if(row.Count==0)return Results.NotFound();
            await ExecTx(conn,(NpgsqlTransaction)tx,
                "DELETE FROM radcheck WHERE username=@u AND attribute='Cleartext-Password'",
                Store.P("u",(string)row[0]["username"]!));
            await ExecTx(conn,(NpgsqlTransaction)tx,
                "UPDATE services SET state='suspended',changed_at=now() WHERE id=@id",
                Store.P("id",id));
            await AuditTx(conn,(NpgsqlTransaction)tx,Actor(ctx),"suspend","service",id.ToString());
            await tx.CommitAsync();
            return Results.Ok(new {ok=true,note="New authentication denied. Active sessions need a separate disconnect."});
        });

        api.MapGet("/radius/nas",async (HttpContext ctx,NpgsqlDataSource db) =>
        {
            if(!Role(ctx,"admin","dispatcher"))return Forbidden();
            // RADIUS shared secrets are NEVER returned in JSON.
            return Results.Ok(await Store.Rows(db,
                "SELECT id,nasname,shortname,type,description FROM nas ORDER BY shortname"));
        });
        api.MapPost("/radius/nas",async (HttpContext ctx,NpgsqlDataSource db,NasInput body) =>
        {
            if(!IsAdmin(ctx))return Forbidden();
            if(!IPAddress.TryParse(body.Address,out var address)||
                address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork ||
                body.Secret?.Length<16||body.Name?.Length is null or 0 or > 80)
                return Invalid("NAS requires IPv4 address, name and shared secret at least 16 characters");
            await using var conn=await db.OpenConnectionAsync();
            await using var cmd=new NpgsqlCommand("""
                INSERT INTO nas(nasname,shortname,type,secret,description,require_ma,limit_proxy_state)
                VALUES(@address,@name,'other',@secret,@description,'yes','yes') RETURNING id
                """,conn);
            cmd.Parameters.AddRange(new NpgsqlParameter[] {
                Store.P("address",body.Address),Store.P("name",body.Name),
                Store.P("secret",body.Secret),Store.P("description",body.Description??"")
            });
            var id=await cmd.ExecuteScalarAsync();
            await Store.Audit(db,Actor(ctx),"create","nas",id?.ToString()??"");
            return Results.Created("/api/radius/nas",new{id,restartRequired=true,
                note="Restart the FreeRADIUS service to load updated SQL NAS clients."});
        });
        api.MapGet("/radius/sessions",async (HttpContext ctx,NpgsqlDataSource db) =>
        {
            if(!Role(ctx,"admin","dispatcher","technician"))return Forbidden();
            return Results.Ok(await Store.Rows(db,"""
                SELECT radacctid,username,nasipaddress::text AS nas_ip,
                       framedipaddress::text AS assigned_ip,callingstationid AS mac,
                       acctstarttime,acctupdatetime,acctstoptime,
                       acctinputoctets,acctoutputoctets,
                       (acctstoptime IS NULL AND acctupdatetime>now()-interval '30 minutes') AS recently_seen
                FROM radacct ORDER BY acctstarttime DESC NULLS LAST LIMIT 500
                """));
        });
        api.MapGet("/radius/auth-log",async (HttpContext ctx,NpgsqlDataSource db) =>
        {
            if(!Role(ctx,"admin","dispatcher","technician"))return Forbidden();
            return Results.Ok(await Store.Rows(db,
                "SELECT username,reply,authdate FROM radpostauth ORDER BY id DESC LIMIT 100"));
        });

        api.MapGet("/tickets",async (NpgsqlDataSource db) =>
            Results.Ok(await Store.Rows(db,"""
                SELECT t.*,c.account,h.name AS highsite FROM tickets t
                LEFT JOIN customers c ON c.id=t.customer_id
                LEFT JOIN highsites h ON h.id=t.highsite_id
                ORDER BY t.created_at DESC LIMIT 2000
                """)));
        api.MapPost("/tickets",async (HttpContext ctx,NpgsqlDataSource db,TicketInput body) =>
        {
            if(!CanEdit(ctx))return Forbidden();
            if(string.IsNullOrWhiteSpace(body.Subject)||body.Subject.Length>200||
                !Allowed(body.Type,"customer","highsite","voip","other")||
                !Allowed(body.Priority,"low","normal","high","critical")||
                (body.CustomerId is null && body.HighsiteId is null))
                return Invalid("Subject, ticket type, priority, and customer or site required");
            var id=await Store.Insert(db,"""
                INSERT INTO tickets(customer_id,highsite_id,subject,description,type,priority)
                VALUES (@c,@h,@sub,@desc,@t,@p) RETURNING id
                """,Store.P("c",body.CustomerId),Store.P("h",body.HighsiteId),
                Store.P("sub",body.Subject.Trim()),Store.P("desc",body.Description??""),
                Store.P("t",body.Type),Store.P("p",body.Priority));
            await Store.Audit(db,Actor(ctx),"create","ticket",id.ToString());
            return Results.Created($"/api/tickets/{id}",new{id});
        });
        api.MapPut("/tickets/{id:guid}/status",async (Guid id,HttpContext ctx,
            NpgsqlDataSource db,TicketStatusInput body) =>
        {
            if(!CanEdit(ctx))return Forbidden();
            if(!Allowed(body.Status,"open","in_progress","pending","resolved","closed"))
                return Invalid("Invalid ticket status");
            var n=await Store.Execute(db,"UPDATE tickets SET status=@s WHERE id=@id",
                Store.P("s",body.Status),Store.P("id",id));
            if(n==0)return Results.NotFound();
            await Store.Audit(db,Actor(ctx),"status","ticket",id.ToString());
            return Results.Ok(new {ok=true});
        });

        api.MapGet("/invoices",async (HttpContext ctx,NpgsqlDataSource db) =>
        {
            if(!Role(ctx,"admin","dispatcher"))return Forbidden();
            return Results.Ok(await Store.Rows(db,"""
                SELECT i.*,c.account,COALESCE(sum(p.amount),0) AS paid,
                       i.total-COALESCE(sum(p.amount),0) AS outstanding
                FROM invoices i JOIN customers c ON c.id=i.customer_id
                LEFT JOIN payments p ON p.invoice_id=i.id
                GROUP BY i.id,c.account ORDER BY i.created_at DESC LIMIT 3000
                """));
        });
        api.MapPost("/invoices",async (HttpContext ctx,NpgsqlDataSource db,InvoiceInput body) =>
        {
            if(!IsAdmin(ctx))return Forbidden();
            if(string.IsNullOrWhiteSpace(body.Number)||body.Number.Length>120||body.Total<0)
                return Invalid("Invoice number and nonnegative total required");
            var id=await Store.Insert(db,"""
                INSERT INTO invoices(customer_id,number,total,due_date)
                VALUES (@c,@n,@t,@due) RETURNING id
                """,Store.P("c",body.CustomerId),Store.P("n",body.Number),
                Store.P("t",body.Total),Store.P("due",body.DueDate));
            await Store.Audit(db,Actor(ctx),"create","invoice",id.ToString());
            return Results.Created($"/api/invoices/{id}",new{id});
        });
        api.MapPost("/billing/monthly-drafts",async (HttpContext ctx,NpgsqlDataSource db,
            BillingPeriodInput body) =>
        {
            if(!IsAdmin(ctx))return Forbidden();
            if(body.Period.Day!=1)return Invalid("Period must be first day of month");
            // Customer/month uniqueness prevents double billing. No automatic issuance.
            var invoiceRows=await Store.Rows(db,"""
                INSERT INTO invoices(customer_id,number,period,total,due_date)
                SELECT s.customer_id,'NF-'||to_char(@period::date,'YYYYMM')||'-'||s.customer_id::text,
                       @period::date,sum(p.monthly_price),(@period::date+interval '7 days')::date
                FROM services s JOIN customers c ON c.id=s.customer_id
                JOIN packages p ON p.id=s.package_id
                WHERE s.state='active' AND c.status='active' AND p.monthly_price>0
                GROUP BY s.customer_id
                ON CONFLICT(customer_id,period) DO NOTHING RETURNING id
                """,Store.P("period",body.Period));
            await Store.Audit(db,Actor(ctx),"draft-monthly","invoice",body.Period.ToString("yyyy-MM",CultureInfo.InvariantCulture));
            return Results.Ok(new {created=invoiceRows.Count,note="Drafts only. No tax calculation, delivery or payment capture."});
        });
        api.MapPost("/invoices/{id:guid}/issue",async (Guid id,HttpContext ctx,NpgsqlDataSource db) =>
        {
            if(!IsAdmin(ctx))return Forbidden();
            var n=await Store.Execute(db,
                "UPDATE invoices SET status='issued' WHERE id=@id AND status='draft'",Store.P("id",id));
            if(n==0)return Results.Conflict(new {error="Invoice must exist and be draft"});
            await Store.Audit(db,Actor(ctx),"issue","invoice",id.ToString());
            return Results.Ok(new {ok=true});
        });
        api.MapPost("/payments",async (HttpContext ctx,NpgsqlDataSource db,PaymentInput body) =>
        {
            if(!IsAdmin(ctx))return Forbidden();
            if(body.Amount<=0||body.Reference?.Length is null or 0 or >160)
                return Invalid("Payment requires a positive amount and reference");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var invoice=await InTransaction(conn,(NpgsqlTransaction)tx,
                "SELECT total,status FROM invoices WHERE id=@id FOR UPDATE",Store.P("id",body.InvoiceId));
            if(invoice.Count==0)return Results.NotFound();
            if((string)invoice[0]["status"]! is not ("issued" or "paid"))
                return Invalid("Only issued invoices accept payments");
            var paid=await InTransaction(conn,(NpgsqlTransaction)tx,
                "SELECT COALESCE(sum(amount),0) AS total FROM payments WHERE invoice_id=@id",
                Store.P("id",body.InvoiceId));
            var outstanding=(decimal)invoice[0]["total"]!-(decimal)paid[0]["total"]!;
            if(body.Amount>outstanding)return Results.Conflict(new {error="Payment exceeds outstanding balance"});
            await ExecTx(conn,(NpgsqlTransaction)tx,"""
                INSERT INTO payments(invoice_id,amount,reference) VALUES(@i,@a,@ref)
                """,Store.P("i",body.InvoiceId),Store.P("a",body.Amount),
                Store.P("ref",body.Reference.Trim()));
            if(body.Amount==outstanding)
                await ExecTx(conn,(NpgsqlTransaction)tx,
                    "UPDATE invoices SET status='paid' WHERE id=@id",Store.P("id",body.InvoiceId));
            await AuditTx(conn,(NpgsqlTransaction)tx,Actor(ctx),"payment","invoice",body.InvoiceId.ToString());
            await tx.CommitAsync();
            return Results.Ok(new {ok=true,remaining=outstanding-body.Amount});
        });
        api.MapGet("/audit",async (HttpContext ctx,NpgsqlDataSource db) =>
        {
            if(!IsAdmin(ctx))return Forbidden();
            return Results.Ok(await Store.Rows(db,
                "SELECT username,action,entity,entity_id,occurred_at FROM audit_log ORDER BY id DESC LIMIT 500"));
        });
    }

    private static bool ValidCustomer(CustomerInput x) =>
        !string.IsNullOrWhiteSpace(x.Account) && x.Account.Length<=100 &&
        !string.IsNullOrWhiteSpace(x.FirstName) && x.FirstName.Length<=120 &&
        Allowed(x.Status,"active","suspended","pending","cancelled") &&
        (x.SectorId is null || x.HighsiteId is not null);

    private static NpgsqlParameter[] CustomerParameters(CustomerInput x) =>
    [
        Store.P("a",x.Account.Trim()),Store.P("n",x.FirstName.Trim()),Store.P("s",x.Surname??""),
        Store.P("e",x.Email??""),Store.P("p",x.Phone??""),Store.P("addr",x.Address??""),
        Store.P("st",x.Status),Store.P("b",x.BranchId),Store.P("h",x.HighsiteId),
        Store.P("sector",x.SectorId)
    ];

    private static bool ValidService(ServiceInput x) =>
        !string.IsNullOrWhiteSpace(x.Username) && x.Username.Length<=180 &&
        Allowed(x.State,"pending","suspended","cancelled","active") &&
        (string.IsNullOrWhiteSpace(x.StaticIpv4) ||
            (IPAddress.TryParse(x.StaticIpv4,out var address) &&
             address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork));

    private static async Task<List<Dictionary<string,object?>>> InTransaction(
        NpgsqlConnection connection,NpgsqlTransaction tx,string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var cmd=new NpgsqlCommand(sql,connection,tx);
        cmd.Parameters.AddRange(parameters);
        return await Store.Read(cmd);
    }

    private static async Task ExecTx(NpgsqlConnection connection,NpgsqlTransaction tx,
        string sql,params NpgsqlParameter[] parameters)
    {
        await using var cmd=new NpgsqlCommand(sql,connection,tx);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task AuditTx(NpgsqlConnection connection,NpgsqlTransaction tx,
        string actor,string action,string entity,string id) =>
        ExecTx(connection,tx,
            "INSERT INTO audit_log(username,action,entity,entity_id) VALUES(@u,@a,@e,@id)",
            Store.P("u",actor),Store.P("a",action),Store.P("e",entity),Store.P("id",id));
}
public sealed record BillingPeriodInput(DateOnly Period);
