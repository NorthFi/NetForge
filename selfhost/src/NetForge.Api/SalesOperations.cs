// root/selfhost/src/NetForge.Api/SalesOperations.cs
// Lead -> immutable quotation -> staff-attested acceptance -> pending CRM
// customer + linked installation ticket/order. No billing/RADIUS activation.
using System.Security.Claims;
using Npgsql;

namespace NetForge.Api;

public sealed record SalesLeadInput(string FirstName,string? Surname,string? Email,string? Phone,
    string Address,Guid BranchId,string? Source,string? Notes);
public sealed record SalesLeadState(string State,bool Confirm);
public sealed record SalesQuoteInput(Guid PackageId,decimal InstallationPrice,int ValidDays,string Terms,bool Confirm);
public sealed record SalesAcceptInput(string Account,string AcceptedByName,string EvidenceReference,bool Confirm);
public sealed record SalesConfirm(bool Confirm);
public sealed record SalesRosterInput(Guid UserId,bool Enabled,bool Confirm);
public sealed record InstallScheduleInput(Guid TechnicianId,DateOnly Day,string? Note,bool Confirm);
public sealed record InstallStateInput(string State,string Note,bool Confirm);

public static class SalesOperations
{
    private static NpgsqlParameter P(string key,object? val)=>Store.P(key,val);
    private static bool Admin(HttpContext c)=>c.User.IsInRole("admin");
    private static bool Staff(HttpContext c)=>Admin(c)||c.User.IsInRole("dispatcher");
    private static bool Technician(HttpContext c)=>c.User.IsInRole("technician");
    private static Guid UserId(HttpContext c)=>Guid.Parse(c.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private static string Actor(HttpContext c)=>c.User.Identity?.Name??"unknown";
    private static IResult Denied()=>Results.StatusCode(403);
    private static IResult Invalid(string error)=>Results.BadRequest(new{error});
    private static IResult Conflict(string error)=>Results.Conflict(new{error});
    private static string Clean(string? s)=>(s??"").Trim();
    private static Task<List<Dictionary<string,object?>>> Lead(NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)=>
        NetworkManagement.Query(conn,tx,"SELECT * FROM sales_leads WHERE id=@id FOR UPDATE",P("id",id));
    private static Task<List<Dictionary<string,object?>>> Order(NpgsqlConnection conn,NpgsqlTransaction tx,Guid id)=>
        NetworkManagement.Query(conn,tx,"SELECT * FROM installation_orders WHERE id=@id FOR UPDATE",P("id",id));
    private static async Task Event(NpgsqlConnection conn,NpgsqlTransaction tx,HttpContext ctx,Guid id,string action,string note)
    {
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO installation_events(order_id,actor_id,action,note) VALUES(@id,@actor,@action,@note)
            """,P("id",id),P("actor",UserId(ctx)),P("action",action),P("note",note));
        await NetworkManagement.Audit(conn,tx,Actor(ctx),"installation-"+action,"installation_order",id.ToString());
    }
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/sales/leads",async(HttpContext ctx,NpgsqlDataSource db,Guid? branch,string? state,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000||state is not (null or "new" or "qualified" or "lost" or "converted"))return Invalid("Invalid filters");
            return Results.Ok(await Store.Rows(db,"""
                SELECT l.*,b.name AS branch,c.account AS customer_account FROM sales_leads l
                JOIN branches b ON b.id=l.branch_id LEFT JOIN customers c ON c.id=l.customer_id
                WHERE (@branch::uuid IS NULL OR l.branch_id=@branch) AND (@state::text IS NULL OR l.state=@state)
                ORDER BY l.created_at DESC,l.id OFFSET @offset LIMIT 100
                """,P("branch",branch),P("state",state),P("offset",offset??0)));
        });
        api.MapPost("/sales/leads",async(HttpContext ctx,NpgsqlDataSource db,SalesLeadInput input)=>
        {
            if(!Staff(ctx))return Denied();
            var first=Clean(input.FirstName);var surname=Clean(input.Surname);var email=Clean(input.Email);
            var phone=Clean(input.Phone);var address=Clean(input.Address);var source=Clean(input.Source);var notes=Clean(input.Notes);
            if(first.Length is <1 or >120||surname.Length>120||email.Length>254||phone.Length>70||
               (email.Length==0&&phone.Length==0)||address.Length is <1 or >500||source.Length>80||notes.Length>2000)
                return Invalid("Name, at least one contact method, address, branch and valid field lengths required");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO sales_leads(first_name,surname,email,phone,address,branch_id,source,notes,created_by)
                VALUES(@first,@surname,@email,@phone,@address,@branch,@source,@notes,@actor) RETURNING id
                """,P("first",first),P("surname",surname),P("email",email),P("phone",phone),P("address",address),
                P("branch",input.BranchId),P("source",source),P("notes",notes),P("actor",UserId(ctx)));
            var id=(Guid)rows[0]["id"]!;
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"sales-lead-create","sales_lead",id.ToString());
            await tx.CommitAsync();
            return Results.Created("/api/sales/leads/"+id,new{id});
        });
        api.MapPost("/sales/leads/{id:guid}/state",async(Guid id,HttpContext ctx,NpgsqlDataSource db,SalesLeadState input)=>
        {
            if(!Staff(ctx))return Denied();
            if(!input.Confirm||input.State is not ("qualified" or "lost"))return Invalid("Confirm qualification or closure as lost");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var rows=await Lead(conn,tx,id);if(rows.Count!=1)return Results.NotFound();
            var old=rows[0]["state"] as string;
            if(old=="converted"||old=="lost"||old==input.State)return Conflict("Lead transition is not allowed");
            if(input.State=="lost")
                await NetworkManagement.Execute(conn,tx,"UPDATE sales_quotes SET state='withdrawn' WHERE lead_id=@id AND state='issued'",P("id",id));
            await NetworkManagement.Execute(conn,tx,"UPDATE sales_leads SET state=@state WHERE id=@id",P("state",input.State),P("id",id));
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"sales-lead-"+input.State,"sales_lead",id.ToString());
            await tx.CommitAsync();return Results.Ok(new{ok=true});
        });
        api.MapGet("/sales/leads/{id:guid}/quotes",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT q.*,a.id AS acceptance_id,a.accepted_by_name,a.evidence_reference,a.recorded_at,
                   c.account AS customer_account,o.id AS installation_id
                FROM sales_quotes q LEFT JOIN sales_acceptances a ON a.quote_id=q.id
                LEFT JOIN customers c ON c.id=a.customer_id LEFT JOIN installation_orders o ON o.acceptance_id=a.id
                WHERE q.lead_id=@id ORDER BY q.issued_at DESC,q.id LIMIT 100
                """,P("id",id)));
        });
        api.MapPost("/sales/leads/{id:guid}/quotes",async(Guid id,HttpContext ctx,NpgsqlDataSource db,SalesQuoteInput input)=>
        {
            if(!Staff(ctx))return Denied();
            var terms=Clean(input.Terms);
            if(!input.Confirm||input.ValidDays is <1 or >90||input.InstallationPrice<0||input.InstallationPrice>9999999999.99m||
               decimal.Round(input.InstallationPrice,2)!=input.InstallationPrice||terms.Length is <5 or >2000)
                return Invalid("Confirm installation price (ZAR), 1–90 validity days and quotation terms");
            await using var conn=await db.OpenConnectionAsync();
            await using var tx=await conn.BeginTransactionAsync();
            var leads=await Lead(conn,tx,id);if(leads.Count!=1)return Results.NotFound();
            if(leads[0]["state"] as string!="qualified")return Conflict("Only qualified leads can receive quotations");
            var counts=await NetworkManagement.Query(conn,tx,"SELECT count(*) AS n FROM sales_quotes WHERE lead_id=@id",P("id",id));
            if((long)counts[0]["n"]!>=100)return Conflict("Lead has reached 100 historical quotations");
            var packages=await NetworkManagement.Query(conn,tx,"SELECT * FROM packages WHERE id=@id FOR SHARE",P("id",input.PackageId));
            if(packages.Count!=1)return Results.NotFound();
            // Explicit withdrawal is required even when the prior quote expired.
            if((await NetworkManagement.Query(conn,tx,"SELECT id FROM sales_quotes WHERE lead_id=@id AND state='issued'",P("id",id))).Count!=0)
                return Conflict("Withdraw the outstanding quotation before issuing a replacement");
            var p=packages[0];var l=leads[0];
            var rows=await NetworkManagement.Query(conn,tx,"""
                INSERT INTO sales_quotes(lead_id,package_id,package_name,download_mbps,upload_mbps,monthly_price,
                    installation_price,contact_name,service_address,terms,valid_until,issued_by)
                VALUES(@lead,@package,@name,@down,@up,@monthly,@install,@contact,@address,@terms,now()+make_interval(days=>@days),@actor)
                RETURNING id
                """,P("lead",id),P("package",input.PackageId),P("name",p["name"]),P("down",p["download_mbps"]),P("up",p["upload_mbps"]),
                P("monthly",p["monthly_price"]),P("install",input.InstallationPrice),P("contact",Clean((string)l["first_name"]!+" "+l["surname"])),
                P("address",l["address"]),P("terms",terms),P("days",input.ValidDays),P("actor",UserId(ctx)));
            var quote=(Guid)rows[0]["id"]!;
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"sales-quote-issue","sales_quote",quote.ToString());
            await tx.CommitAsync();return Results.Created("/api/sales/quotes/"+quote,new{id=quote});
        });
        api.MapPost("/sales/leads/{id:guid}/quotes/{quote:guid}/withdraw",async(Guid id,Guid quote,HttpContext ctx,NpgsqlDataSource db,SalesConfirm input)=>
        {
            if(!Staff(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm withdrawal");
            await using var conn=await db.OpenConnectionAsync();await using var tx=await conn.BeginTransactionAsync();
            if((await Lead(conn,tx,id)).Count!=1)return Results.NotFound();
            var rows=await NetworkManagement.Query(conn,tx,"UPDATE sales_quotes SET state='withdrawn' WHERE id=@quote AND lead_id=@id AND state='issued' RETURNING id",P("quote",quote),P("id",id));
            if(rows.Count!=1)return Conflict("Only an outstanding quotation can be withdrawn");
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"sales-quote-withdraw","sales_quote",quote.ToString());
            await tx.CommitAsync();return Results.Ok(new{ok=true});
        });
        api.MapPost("/sales/leads/{id:guid}/quotes/{quote:guid}/accept",Accept);
        api.MapGet("/installations/roster",async(HttpContext ctx,NpgsqlDataSource db,Guid branch)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT u.id,u.username,u.enabled,r.user_id IS NOT NULL AS assigned
                FROM app_users u LEFT JOIN branch_technicians r ON r.user_id=u.id AND r.branch_id=@branch
                WHERE u.role='technician' OR r.user_id IS NOT NULL ORDER BY u.username LIMIT 1000
                """,P("branch",branch)));
        });
        api.MapPut("/installations/roster/{branch:guid}",async(Guid branch,HttpContext ctx,NpgsqlDataSource db,SalesRosterInput input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm roster change");
            await using var conn=await db.OpenConnectionAsync();await using var tx=await conn.BeginTransactionAsync();
            // Roster and scheduling serialize on the branch row. No assignment
            // may survive removal from a branch while a work order is open.
            if((await NetworkManagement.Query(conn,tx,"SELECT id FROM branches WHERE id=@id FOR UPDATE",P("id",branch))).Count!=1)return Results.NotFound();
            if(input.Enabled)
            {
                if((await NetworkManagement.Query(conn,tx,"SELECT id FROM app_users WHERE id=@id AND enabled AND role='technician' FOR SHARE",P("id",input.UserId))).Count!=1)
                    return Conflict("An enabled technician account is required");
                await NetworkManagement.Execute(conn,tx,"INSERT INTO branch_technicians(branch_id,user_id) VALUES(@branch,@user) ON CONFLICT DO NOTHING",P("branch",branch),P("user",input.UserId));
            }
            else
            {
                if((await NetworkManagement.Query(conn,tx,"SELECT id FROM installation_orders WHERE branch_id=@branch AND technician_id=@user AND state IN ('scheduled','in_progress','blocked') LIMIT 1",P("branch",branch),P("user",input.UserId))).Count!=0)
                    return Conflict("Resolve or reassign open work before removing technician");
                await NetworkManagement.Execute(conn,tx,"DELETE FROM branch_technicians WHERE branch_id=@branch AND user_id=@user",P("branch",branch),P("user",input.UserId));
            }
            await NetworkManagement.Audit(conn,tx,Actor(ctx),"installation-roster","branch",branch.ToString());
            await tx.CommitAsync();return Results.Ok(new{ok=true});
        });
        api.MapGet("/installations/orders",async(HttpContext ctx,NpgsqlDataSource db,Guid? branch,DateOnly? day,bool? unscheduled,int? offset)=>
        {
            if(!Staff(ctx)&&!Technician(ctx))return Denied();
            if(offset is <0 or >1000000||day.HasValue&&unscheduled==true)return Invalid("Invalid planning filters");
            return Results.Ok(await Store.Rows(db,"""
                SELECT o.*,c.account,c.first_name,c.surname,c.phone,c.address,q.package_name,q.number AS quote_number,
                    t.number AS ticket_number,b.name AS branch,u.username AS technician
                FROM installation_orders o JOIN sales_acceptances a ON a.id=o.acceptance_id
                JOIN customers c ON c.id=a.customer_id JOIN sales_quotes q ON q.id=a.quote_id
                JOIN tickets t ON t.id=o.ticket_id JOIN branches b ON b.id=o.branch_id
                LEFT JOIN app_users u ON u.id=o.technician_id
                WHERE (@staff OR o.technician_id=@actor)
                  AND (@branch::uuid IS NULL OR o.branch_id=@branch)
                  AND (@day::date IS NULL OR o.scheduled_day=@day)
                  AND (NOT @unscheduled OR o.state='unscheduled')
                ORDER BY o.scheduled_day NULLS FIRST,o.number OFFSET @offset LIMIT 100
                """,P("staff",Staff(ctx)),P("actor",UserId(ctx)),P("branch",branch),P("day",day),
                P("unscheduled",unscheduled==true),P("offset",offset??0)));
        });
        api.MapGet("/installations/orders/{id:guid}/events",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx)&&!Technician(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT e.id,e.action,e.note,e.occurred_at,u.username FROM installation_events e
                JOIN installation_orders o ON o.id=e.order_id JOIN app_users u ON u.id=e.actor_id
                WHERE o.id=@id AND (@staff OR o.technician_id=@actor) ORDER BY e.id DESC LIMIT 200
                """,P("id",id),P("staff",Staff(ctx)),P("actor",UserId(ctx))));
        });
        api.MapPost("/installations/orders/{id:guid}/schedule",Schedule);
        api.MapPost("/installations/orders/{id:guid}/state",Transition);
    }
    private static async Task<IResult> Accept(Guid id,Guid quote,HttpContext ctx,NpgsqlDataSource db,SalesAcceptInput input)
    {
        if(!Staff(ctx))return Denied();
        var account=Clean(input.Account);var name=Clean(input.AcceptedByName);var reference=Clean(input.EvidenceReference);
        if(!input.Confirm||account.Length is <1 or >100||name.Length is <1 or >160||reference.Length is <5 or >240)
            return Invalid("Confirm customer acceptance evidence, account and accepting party");
        await using var conn=await db.OpenConnectionAsync();await using var tx=await conn.BeginTransactionAsync();
        var leads=await Lead(conn,tx,id);if(leads.Count!=1)return Results.NotFound();
        var l=leads[0];
        if(l["state"] as string!="qualified")return Conflict("Lead already converted or is not qualified");
        var quotes=await NetworkManagement.Query(conn,tx,"SELECT * FROM sales_quotes WHERE id=@quote AND lead_id=@id AND state='issued' AND valid_until>now() FOR UPDATE",P("id",id),P("quote",quote));
        if(quotes.Count!=1)return Conflict("Quotation must be issued and unexpired");
        var q=quotes[0];
        // Existing billing uses current package prices. Reject a changed package
        // at acceptance rather than silently promise a conflicting tariff.
        var pk=await NetworkManagement.Query(conn,tx,"SELECT name,monthly_price,download_mbps,upload_mbps FROM packages WHERE id=@id FOR SHARE",P("id",q["package_id"]));
        if(pk.Count!=1||!Equals(pk[0]["monthly_price"],q["monthly_price"])||!Equals(pk[0]["download_mbps"],q["download_mbps"])||
           !Equals(pk[0]["upload_mbps"],q["upload_mbps"])||!Equals(pk[0]["name"],q["package_name"]))
            return Conflict("Package changed since issue; withdraw and reissue the quotation");
        var customer=Guid.NewGuid();var acceptance=Guid.NewGuid();var ticket=Guid.NewGuid();var order=Guid.NewGuid();
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO customers(id,account,first_name,surname,email,phone,address,status,branch_id)
            VALUES(@id,@account,@first,@surname,@email,@phone,@address,'pending',@branch)
            """,P("id",customer),P("account",account),P("first",l["first_name"]),P("surname",l["surname"]),
            P("email",l["email"]),P("phone",l["phone"]),P("address",q["service_address"]),P("branch",l["branch_id"]));
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO sales_acceptances(id,quote_id,customer_id,accepted_by_name,evidence_reference,recorded_by)
            VALUES(@id,@quote,@customer,@name,@reference,@actor)
            """,P("id",acceptance),P("quote",quote),P("customer",customer),P("name",name),P("reference",reference),P("actor",UserId(ctx)));
        await NetworkManagement.Execute(conn,tx,"UPDATE sales_quotes SET state='accepted' WHERE id=@id",P("id",quote));
        await NetworkManagement.Execute(conn,tx,"UPDATE sales_leads SET state='converted',customer_id=@customer WHERE id=@id",P("customer",customer),P("id",id));
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO tickets(id,customer_id,subject,description,type,priority)
            VALUES(@id,@customer,@subject,@description,'customer','normal')
            """,P("id",ticket),P("customer",customer),P("subject","New service installation"),
            P("description","Accepted quotation Q-"+q["number"]+"; verify installation and coverage before separate service activation."));
        await NetworkManagement.Execute(conn,tx,"""
            INSERT INTO installation_orders(id,acceptance_id,ticket_id,branch_id)
            VALUES(@id,@acceptance,@ticket,@branch)
            """,P("id",order),P("acceptance",acceptance),P("ticket",ticket),P("branch",l["branch_id"]));
        await Event(conn,tx,ctx,order,"created","Created from accepted quotation; no service activation");
        await NetworkManagement.Audit(conn,tx,Actor(ctx),"sales-quote-accept","sales_quote",quote.ToString());
        await tx.CommitAsync();
        return Results.Created("/api/installations/orders/"+order,new{id=order,customerId=customer,ticketId=ticket,acceptanceId=acceptance,note="Pending customer only; no invoices or RADIUS service created"});
    }
    private static async Task<IResult> Schedule(Guid id,HttpContext ctx,NpgsqlDataSource db,InstallScheduleInput input)
    {
        if(!Staff(ctx))return Denied();
        var note=Clean(input.Note);
        if(!input.Confirm||input.Day.Year is <2000 or >2100||note.Length>940)return Invalid("Confirm technician, planning date and a note of at most 940 characters");
        await using var conn=await db.OpenConnectionAsync();await using var tx=await conn.BeginTransactionAsync();
        var rows=await Order(conn,tx,id);if(rows.Count!=1)return Results.NotFound();
        var o=rows[0];
        if(o["state"] as string is not ("unscheduled" or "scheduled" or "blocked"))return Conflict("Only unstarted or blocked orders may be scheduled");
        await NetworkManagement.Query(conn,tx,"SELECT id FROM branches WHERE id=@id FOR UPDATE",P("id",o["branch_id"]));
        if((await NetworkManagement.Query(conn,tx,"""
            SELECT r.id FROM stock_reservations r JOIN stock_warehouses w ON w.id=r.warehouse_id
            WHERE r.order_id=@id AND r.state='reserved' AND w.technician_id IS NOT NULL AND w.technician_id<>@tech LIMIT 1
            """,P("id",id),P("tech",input.TechnicianId))).Count!=0)
            return Conflict("Release previous technician stock reservations before reassignment");
        if((await NetworkManagement.Query(conn,tx,"""
            SELECT u.id FROM branch_technicians r JOIN app_users u ON u.id=r.user_id
            WHERE r.branch_id=@branch AND r.user_id=@user AND u.role='technician' AND u.enabled FOR SHARE OF u
            """,P("branch",o["branch_id"]),P("user",input.TechnicianId))).Count!=1)
            return Conflict("Technician must be enabled and assigned to this branch");
        await NetworkManagement.Execute(conn,tx,"UPDATE installation_orders SET state='scheduled',scheduled_day=@day,technician_id=@user WHERE id=@id",
            P("day",input.Day),P("user",input.TechnicianId),P("id",id));
        // Reserve space for the structured date and assignment in the event note.
        await Event(conn,tx,ctx,id,"scheduled",input.Day.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture)+" / "+input.TechnicianId+" "+note);
        await tx.CommitAsync();return Results.Ok(new{ok=true});
    }
    private static async Task<IResult> Transition(Guid id,HttpContext ctx,NpgsqlDataSource db,InstallStateInput input)
    {
        if(!Staff(ctx)&&!Technician(ctx))return Denied();
        var note=Clean(input.Note);
        if(!input.Confirm||note.Length is <5 or >1000)return Invalid("Confirm outcome with a 5–1000 character note");
        await using var conn=await db.OpenConnectionAsync();await using var tx=await conn.BeginTransactionAsync();
        var rows=await Order(conn,tx,id);if(rows.Count!=1)return Results.NotFound();
        var o=rows[0];var old=(string)o["state"]!;
        if(!Staff(ctx)&&(!Equals(o["technician_id"],UserId(ctx))||input.State=="cancelled"))return Denied();
        var allowed=(old,input.State) switch {
            ("scheduled","in_progress")=>true,
            ("scheduled","blocked")=>true,
            ("in_progress","completed")=>true,
            ("in_progress","blocked")=>true,
            ("unscheduled" or "scheduled" or "blocked","cancelled") when Staff(ctx)=>true,
            _=>false
        };
        if(!allowed)return Conflict("Installation state transition not permitted");
        // The order lock serializes against warehouse reservation/issue.
        // Never close an order with allocated stock still outstanding.
        if(input.State is ("completed" or "cancelled")&&
            (await NetworkManagement.Query(conn,tx,"SELECT id FROM stock_reservations WHERE order_id=@id AND state='reserved' LIMIT 1",P("id",id))).Count!=0)
            return Conflict("Issue or release all stock reservations before closing the installation");
        await NetworkManagement.Execute(conn,tx,"""
            UPDATE installation_orders SET state=@state,result_note=@note,
                completed_at=CASE WHEN @state='completed' THEN now() ELSE NULL END WHERE id=@id
            """,P("state",input.State),P("note",note),P("id",id));
        // The linked ticket is independently managed. Do not overwrite a
        // separately closed/reopened ticket or imply that service is active.
        await Event(conn,tx,ctx,id,input.State,note);
        await tx.CommitAsync();return Results.Ok(new{state=input.State,note="Work order updated; ticket, customer and billing lifecycle remain separate"});
    }
}
