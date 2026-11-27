// root/selfhost/src/NetForge.Api/Warehouse.cs
// All inventory writes share one transaction-scoped advisory lock. Order
// operations lock the installation first, matching SalesOperations. This
// intentionally serializes stock writes, avoiding multi-warehouse deadlocks.
using System.Security.Claims;
using Npgsql;

namespace NetForge.Api;
public sealed record StockItemInput(string Sku,string Name,string? Model,string Category,string Unit,bool Serialized,int MinAvailable);
public sealed record StockWarehouseInput(Guid BranchId,string Name,Guid? TechnicianId);
public sealed record StockMoveInput(Guid RequestId,Guid ItemId,Guid WarehouseId,Guid? DestinationId,
    int Quantity,string[]? Serials,string Reference,bool Confirm);
public sealed record StockReserveInput(Guid RequestId,Guid OrderId,Guid WarehouseId,Guid ItemId,
    int Quantity,string[]? Serials,string Reference,bool Confirm);
public sealed record StockResolveInput(Guid RequestId,string Reference,bool Confirm);

public static class Warehouse
{
    private static NpgsqlParameter P(string key,object? value)=>Store.P(key,value);
    private static bool Admin(HttpContext ctx)=>ctx.User.IsInRole("admin");
    private static bool Staff(HttpContext ctx)=>Admin(ctx)||ctx.User.IsInRole("dispatcher");
    private static Guid User(HttpContext ctx)=>Guid.Parse(ctx.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private static string Actor(HttpContext ctx)=>ctx.User.Identity?.Name??"unknown";
    private static IResult Denied()=>Results.StatusCode(403);
    private static IResult Invalid(string error)=>Results.BadRequest(new{error});
    private static IResult Conflict(string error)=>Results.Conflict(new{error});
    private static string Clean(string? s)=>(s??"").Trim();
    private static bool Basic(Guid request,int quantity,string reference,bool confirm)=>
        request!=Guid.Empty&&quantity is >=1 and <=100000&&reference.Length is >=3 and <=240&&confirm;
    internal static string[] Serials(string[]? raw)=>(raw??[]).Select(x=>Clean(x).ToUpperInvariant()).ToArray();
    internal static bool ValidSerials(bool tracked,int qty,string[] serials)=>
        tracked?qty<=100&&serials.Length==qty&&serials.All(s=>s.Length is >=1 and <=160&&s.All(c=>c is >= '!' and <= '~'))&&
        serials.Distinct(StringComparer.Ordinal).Count()==qty:serials.Length==0;
    private static Task<List<Dictionary<string,object?>>> Query(NpgsqlConnection c,NpgsqlTransaction t,string sql,params NpgsqlParameter[] p)=>
        NetworkManagement.Query(c,t,sql,p);
    internal static async Task Lock(NpgsqlConnection c,NpgsqlTransaction t)
    {
        await NetworkManagement.Execute(c,t,"SELECT pg_advisory_xact_lock(3100323)");
    }
    internal static async Task<bool> Replayed(NpgsqlConnection c,NpgsqlTransaction t,Guid id)=>
        (await Query(c,t,"SELECT id FROM stock_movements WHERE request_id=@id",P("id",id))).Count!=0;
    internal static async Task<bool> HasAvailable(NpgsqlConnection c,NpgsqlTransaction t,Guid warehouse,Guid item,int qty)=>
        (await Query(c,t,"SELECT item_id FROM stock_balances WHERE warehouse_id=@w AND item_id=@i AND on_hand-reserved>=@q",
            P("w",warehouse),P("i",item),P("q",qty))).Count==1;
    internal static async Task<bool> AvailableSerials(NpgsqlConnection c,NpgsqlTransaction t,Guid warehouse,Guid item,string[] serials)
    {
        if(serials.Length==0)return true;
        return (await Query(c,t,"""
            SELECT id FROM stock_serials WHERE warehouse_id=@w AND item_id=@i AND serial=ANY(@serials)
              AND reservation_id IS NULL AND equipment_id IS NULL
            """,P("w",warehouse),P("i",item),P("serials",serials))).Count==serials.Length;
    }
    internal static async Task Credit(NpgsqlConnection c,NpgsqlTransaction t,Guid w,Guid i,int q)
    {
        await NetworkManagement.Execute(c,t,"""
            INSERT INTO stock_balances(warehouse_id,item_id,on_hand) VALUES(@w,@i,@q)
            ON CONFLICT(warehouse_id,item_id) DO UPDATE SET on_hand=stock_balances.on_hand+EXCLUDED.on_hand
            """,P("w",w),P("i",i),P("q",q));
    }
    internal static async Task Movement(NpgsqlConnection c,NpgsqlTransaction t,HttpContext ctx,Guid request,string action,
        Guid item,Guid? from,Guid? to,Guid? reservation,int qty,string[] serials,string reference)
    {
        await NetworkManagement.Execute(c,t,"""
            INSERT INTO stock_movements(request_id,action,item_id,from_warehouse,to_warehouse,reservation_id,
              quantity,serials,reference,actor_id) VALUES(@request,@action,@item,@from,@to,@r,@q,@s,@ref,@actor)
            """,P("request",request),P("action",action),P("item",item),P("from",from),P("to",to),
            P("r",reservation),P("q",qty),P("s",serials),P("ref",reference),P("actor",User(ctx)));
        await NetworkManagement.Audit(c,t,Actor(ctx),"stock-"+action,"stock_movement",request.ToString());
    }
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/stock/items",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"SELECT * FROM stock_items ORDER BY sku LIMIT 2000"));
        });
        api.MapPost("/stock/items",async(HttpContext ctx,NpgsqlDataSource db,StockItemInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var sku=Clean(input.Sku).ToUpperInvariant();var name=Clean(input.Name);var model=Clean(input.Model);var unit=Clean(input.Unit);
            if(sku.Length is <1 or >80||!sku.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '.')||
               name.Length is <1 or >160||model.Length>160||unit.Length is <1 or >20||input.MinAvailable is <0 or >100000000||
               input.Category is not ("distribution" or "backhaul" or "access" or "power" or "cpe" or "router" or "other"))
                return Invalid("Invalid catalogue item");
            await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
            var rows=await Query(c,t,"""
                INSERT INTO stock_items(sku,name,model,category,unit,serialized,min_available)
                VALUES(@sku,@name,@model,@category,@unit,@serialized,@min) RETURNING id
                """,P("sku",sku),P("name",name),P("model",model),P("category",input.Category),P("unit",unit),
                P("serialized",input.Serialized),P("min",input.MinAvailable));
            var id=(Guid)rows[0]["id"]!;
            await NetworkManagement.Audit(c,t,Actor(ctx),"stock-item-create","stock_item",id.ToString());
            await t.CommitAsync();return Results.Created("/api/stock/items/"+id,new{id});
        });
        api.MapGet("/stock/warehouses",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"""
                SELECT w.*,b.name AS branch,u.username AS technician
                FROM stock_warehouses w JOIN branches b ON b.id=w.branch_id
                LEFT JOIN app_users u ON u.id=w.technician_id ORDER BY b.name,w.name LIMIT 2000
                """));
        });
        api.MapPost("/stock/warehouses",async(HttpContext ctx,NpgsqlDataSource db,StockWarehouseInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=Clean(input.Name);if(name.Length is <1 or >160)return Invalid("Warehouse name required");
            await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
            if((await Query(c,t,"SELECT id FROM branches WHERE id=@id FOR UPDATE",P("id",input.BranchId))).Count!=1)return Results.NotFound();
            if(input.TechnicianId is Guid tech&&(await Query(c,t,"""
                SELECT u.id FROM branch_technicians r JOIN app_users u ON u.id=r.user_id
                WHERE r.branch_id=@branch AND r.user_id=@tech AND u.enabled AND u.role='technician' FOR SHARE OF u
                """,P("branch",input.BranchId),P("tech",tech))).Count!=1)
                return Conflict("Technician must be enabled and assigned to branch");
            var rows=await Query(c,t,"INSERT INTO stock_warehouses(branch_id,name,technician_id) VALUES(@b,@n,@u) RETURNING id",
                P("b",input.BranchId),P("n",name),P("u",input.TechnicianId));
            var id=(Guid)rows[0]["id"]!;
            await NetworkManagement.Audit(c,t,Actor(ctx),"stock-warehouse-create","stock_warehouse",id.ToString());
            await t.CommitAsync();return Results.Created("/api/stock/warehouses/"+id,new{id});
        });
        api.MapGet("/stock/balances",async(HttpContext ctx,NpgsqlDataSource db,Guid? warehouse,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT b.*,i.sku,i.name,i.unit,i.serialized,i.min_available,w.name AS warehouse,
                    b.on_hand-b.reserved AS available,(b.on_hand-b.reserved)<i.min_available AS low_stock
                FROM stock_balances b JOIN stock_items i ON i.id=b.item_id JOIN stock_warehouses w ON w.id=b.warehouse_id
                WHERE (@w::uuid IS NULL OR b.warehouse_id=@w)
                ORDER BY w.name,i.sku,b.warehouse_id OFFSET @offset LIMIT 100
                """,P("w",warehouse),P("offset",offset??0)));
        });
        api.MapGet("/stock/serials",async(HttpContext ctx,NpgsqlDataSource db,Guid item,Guid? warehouse,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT s.id,s.serial,s.warehouse_id,s.reservation_id,s.equipment_id,s.condition,w.name AS warehouse,
                    (SELECT r.state FROM equipment_recoveries r WHERE r.equipment_id=s.equipment_id) AS recovery_state
                FROM stock_serials s LEFT JOIN stock_warehouses w ON w.id=s.warehouse_id
                WHERE s.item_id=@i AND (@w::uuid IS NULL OR s.warehouse_id=@w)
                ORDER BY s.serial OFFSET @offset LIMIT 100
                """,P("i",item),P("w",warehouse),P("offset",offset??0)));
        });
        api.MapGet("/stock/movements",async(HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT m.*,i.sku,f.name AS from_name,d.name AS to_name,u.username
                FROM stock_movements m JOIN stock_items i ON i.id=m.item_id
                LEFT JOIN stock_warehouses f ON f.id=m.from_warehouse LEFT JOIN stock_warehouses d ON d.id=m.to_warehouse
                JOIN app_users u ON u.id=m.actor_id ORDER BY m.id DESC OFFSET @offset LIMIT 100
                """,P("offset",offset??0)));
        });
        // Read-only reconciliation: a single SQL snapshot compares balances
        // with independent movement, reservation and serial evidence. No
        // automatic repair is attempted; investigate every discrepancy.
        api.MapGet("/stock/reconciliation",async(HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                WITH movement_deltas AS (
                    SELECT to_warehouse AS warehouse_id,item_id,quantity::bigint AS delta
                    FROM stock_movements WHERE action IN ('receive','transfer')
                    UNION ALL
                    SELECT from_warehouse,item_id,-quantity::bigint
                    FROM stock_movements WHERE action IN ('transfer','issue')
                ), ledger AS (
                    SELECT warehouse_id,item_id,sum(delta)::bigint AS expected_on_hand
                    FROM movement_deltas GROUP BY warehouse_id,item_id
                ), reservations AS (
                    SELECT warehouse_id,item_id,sum(quantity) AS expected_reserved
                    FROM stock_reservations WHERE state='reserved' GROUP BY warehouse_id,item_id
                ), serials AS (
                    SELECT warehouse_id,item_id,count(*) AS serial_on_hand,
                        count(*) FILTER(WHERE reservation_id IS NOT NULL) AS serial_reserved
                    FROM stock_serials WHERE warehouse_id IS NOT NULL GROUP BY warehouse_id,item_id
                ), keys AS (
                    SELECT warehouse_id,item_id FROM stock_balances
                    UNION SELECT warehouse_id,item_id FROM ledger
                    UNION SELECT warehouse_id,item_id FROM reservations
                    UNION SELECT warehouse_id,item_id FROM serials
                )
                SELECT k.warehouse_id,k.item_id,w.name AS warehouse,i.sku,i.serialized,
                    COALESCE(b.on_hand,0) AS on_hand,COALESCE(b.reserved,0) AS reserved,
                    COALESCE(l.expected_on_hand,0) AS ledger_on_hand,
                    COALESCE(r.expected_reserved,0) AS reservation_total,
                    COALESCE(s.serial_on_hand,0) AS serial_on_hand,
                    COALESCE(s.serial_reserved,0) AS serial_reserved
                FROM keys k JOIN stock_items i ON i.id=k.item_id
                JOIN stock_warehouses w ON w.id=k.warehouse_id
                LEFT JOIN stock_balances b ON b.warehouse_id=k.warehouse_id AND b.item_id=k.item_id
                LEFT JOIN ledger l ON l.warehouse_id=k.warehouse_id AND l.item_id=k.item_id
                LEFT JOIN reservations r ON r.warehouse_id=k.warehouse_id AND r.item_id=k.item_id
                LEFT JOIN serials s ON s.warehouse_id=k.warehouse_id AND s.item_id=k.item_id
                WHERE b.warehouse_id IS NULL
                   OR b.on_hand<>COALESCE(l.expected_on_hand,0)
                   OR b.reserved<>COALESCE(r.expected_reserved,0)
                   OR (i.serialized AND (b.on_hand<>COALESCE(s.serial_on_hand,0)
                     OR b.reserved<>COALESCE(s.serial_reserved,0)))
                   OR (NOT i.serialized AND COALESCE(s.serial_on_hand,0)<>0)
                ORDER BY w.name,i.sku,k.warehouse_id OFFSET @offset LIMIT 100
                """,P("offset",offset??0)));
        });
        api.MapGet("/stock/reservations",async(HttpContext ctx,NpgsqlDataSource db,Guid? order,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT r.*,o.number AS order_number,i.sku,w.name AS warehouse,
                    COALESCE((SELECT m.serials FROM stock_movements m WHERE m.request_id=r.id AND m.action='reserve'),'{}'::text[]) AS serials
                FROM stock_reservations r JOIN installation_orders o ON o.id=r.order_id
                JOIN stock_items i ON i.id=r.item_id JOIN stock_warehouses w ON w.id=r.warehouse_id
                WHERE (@o::uuid IS NULL OR r.order_id=@o) ORDER BY r.created_at DESC,r.id OFFSET @offset LIMIT 100
                """,P("o",order),P("offset",offset??0)));
        });
        api.MapPost("/stock/receive",(HttpContext ctx,NpgsqlDataSource db,StockMoveInput input)=>Move(ctx,db,input,false));
        api.MapPost("/stock/transfer",(HttpContext ctx,NpgsqlDataSource db,StockMoveInput input)=>Move(ctx,db,input,true));
        api.MapPost("/stock/reservations",Reserve);
        api.MapPost("/stock/reservations/{id:guid}/release",(Guid id,HttpContext ctx,NpgsqlDataSource db,StockResolveInput input)=>Resolve(id,ctx,db,input,false));
        api.MapPost("/stock/reservations/{id:guid}/issue",(Guid id,HttpContext ctx,NpgsqlDataSource db,StockResolveInput input)=>Resolve(id,ctx,db,input,true));
    }
    private static async Task<IResult> Move(HttpContext ctx,NpgsqlDataSource db,StockMoveInput input,bool transfer)
    {
        if(!Staff(ctx))return Denied();
        var reference=Clean(input.Reference);
        if(!Basic(input.RequestId,input.Quantity,reference,input.Confirm)||
            (transfer?(input.DestinationId is null||input.DestinationId==input.WarehouseId):input.DestinationId is not null))
            return Invalid("Confirm quantity, request ID, reference and valid warehouse selection");
        var serials=Serials(input.Serials);
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();await Lock(c,t);
        if(await Replayed(c,t,input.RequestId))return Conflict("Request already recorded; refresh movement history");
        var items=await Query(c,t,"SELECT serialized FROM stock_items WHERE id=@id",P("id",input.ItemId));
        if(items.Count!=1)return Results.NotFound();
        if(!ValidSerials((bool)items[0]["serialized"]!,input.Quantity,serials))return Invalid("Serialized stock requires 1–100 unique explicit serials; bulk stock must omit serials");
        if((await Query(c,t,"SELECT id FROM stock_warehouses WHERE id=@id",P("id",input.WarehouseId))).Count!=1)return Results.NotFound();
        var item=input.ItemId;var source=input.WarehouseId;var qty=input.Quantity;
        if(transfer)
        {
            var dest=input.DestinationId!.Value;
            if((await Query(c,t,"SELECT id FROM stock_warehouses WHERE id=@id",P("id",dest))).Count!=1)return Results.NotFound();
            if(!await HasAvailable(c,t,source,item,qty)||!await AvailableSerials(c,t,source,item,serials))return Conflict("Insufficient unreserved stock or unavailable serials");
            await NetworkManagement.Execute(c,t,"UPDATE stock_balances SET on_hand=on_hand-@q WHERE warehouse_id=@w AND item_id=@i",P("q",qty),P("w",source),P("i",item));
            await Credit(c,t,dest,item,qty);
            if(serials.Length>0)await NetworkManagement.Execute(c,t,"UPDATE stock_serials SET warehouse_id=@dest WHERE warehouse_id=@w AND item_id=@i AND serial=ANY(@s)",
                P("dest",dest),P("w",source),P("i",item),P("s",serials));
            await Movement(c,t,ctx,input.RequestId,"transfer",item,source,dest,null,qty,serials,reference);
        }
        else
        {
            await ReceiveLocked(c,t,ctx,input.RequestId,item,source,qty,serials,reference);
        }
        await t.CommitAsync();return Results.Ok(new{ok=true,requestId=input.RequestId});
    }
    // Caller must hold the inventory advisory lock and validate quantity,
    // serials, warehouse and replay. Procurement uses this exact same writer.
    internal static async Task ReceiveLocked(NpgsqlConnection c,NpgsqlTransaction t,HttpContext ctx,Guid request,
        Guid item,Guid warehouse,int qty,string[] serials,string reference)
    {
        await Credit(c,t,warehouse,item,qty);
        foreach(var serial in serials)
            await NetworkManagement.Execute(c,t,"INSERT INTO stock_serials(item_id,serial,warehouse_id) VALUES(@i,@s,@w)",
                P("i",item),P("s",serial),P("w",warehouse));
        await Movement(c,t,ctx,request,"receive",item,null,warehouse,null,qty,serials,reference);
    }
    private static async Task<IResult> Reserve(HttpContext ctx,NpgsqlDataSource db,StockReserveInput input)
    {
        if(!Staff(ctx))return Denied();
        var reference=Clean(input.Reference);var serials=Serials(input.Serials);
        if(!Basic(input.RequestId,input.Quantity,reference,input.Confirm))return Invalid("Confirm quantity, request ID and reference");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        var orders=await Query(c,t,"SELECT state,branch_id,technician_id FROM installation_orders WHERE id=@id FOR UPDATE",P("id",input.OrderId));
        if(orders.Count!=1)return Results.NotFound();
        var order=orders[0];
        if(order["state"] as string is not ("unscheduled" or "scheduled" or "in_progress" or "blocked"))return Conflict("Installation is closed");
        await Lock(c,t);if(await Replayed(c,t,input.RequestId))return Conflict("Request already recorded");
        var items=await Query(c,t,"SELECT serialized FROM stock_items WHERE id=@id",P("id",input.ItemId));
        var warehouses=await Query(c,t,"SELECT branch_id,technician_id FROM stock_warehouses WHERE id=@id",P("id",input.WarehouseId));
        if(items.Count!=1||warehouses.Count!=1)return Results.NotFound();
        var w=warehouses[0];
        if(!Equals(w["branch_id"],order["branch_id"])||(w["technician_id"] is Guid tech&&!Equals(tech,order["technician_id"])))
            return Conflict("Stock must belong to installation branch and, for technician stock, assigned technician");
        if(!ValidSerials((bool)items[0]["serialized"]!,input.Quantity,serials))return Invalid("Invalid serial selection");
        if(!await HasAvailable(c,t,input.WarehouseId,input.ItemId,input.Quantity)||
            !await AvailableSerials(c,t,input.WarehouseId,input.ItemId,serials))return Conflict("Insufficient unreserved stock or unavailable serials");
        await NetworkManagement.Execute(c,t,"""
            INSERT INTO stock_reservations(id,order_id,warehouse_id,item_id,quantity) VALUES(@id,@o,@w,@i,@q)
            """,P("id",input.RequestId),P("o",input.OrderId),P("w",input.WarehouseId),P("i",input.ItemId),P("q",input.Quantity));
        await NetworkManagement.Execute(c,t,"UPDATE stock_balances SET reserved=reserved+@q WHERE warehouse_id=@w AND item_id=@i",
            P("q",input.Quantity),P("w",input.WarehouseId),P("i",input.ItemId));
        if(serials.Length>0)await NetworkManagement.Execute(c,t,"UPDATE stock_serials SET reservation_id=@id WHERE warehouse_id=@w AND item_id=@i AND serial=ANY(@s)",
            P("id",input.RequestId),P("w",input.WarehouseId),P("i",input.ItemId),P("s",serials));
        await Movement(c,t,ctx,input.RequestId,"reserve",input.ItemId,input.WarehouseId,null,input.RequestId,input.Quantity,serials,reference);
        await t.CommitAsync();return Results.Created("/api/stock/reservations/"+input.RequestId,new{id=input.RequestId});
    }
    private static async Task<IResult> Resolve(Guid id,HttpContext ctx,NpgsqlDataSource db,StockResolveInput input,bool issue)
    {
        if(!Staff(ctx))return Denied();
        var reference=Clean(input.Reference);
        if(!Basic(input.RequestId,1,reference,input.Confirm))return Invalid("Confirm request ID and reference");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        var initial=await Query(c,t,"SELECT order_id FROM stock_reservations WHERE id=@id",P("id",id));
        if(initial.Count!=1)return Results.NotFound();
        var orders=await Query(c,t,"""
            SELECT o.state,o.technician_id,a.customer_id FROM installation_orders o
            JOIN sales_acceptances a ON a.id=o.acceptance_id WHERE o.id=@id FOR UPDATE OF o
            """,P("id",initial[0]["order_id"]));
        if(orders.Count!=1)return Results.NotFound();
        await Lock(c,t);if(await Replayed(c,t,input.RequestId))return Conflict("Request already recorded");
        var rows=await Query(c,t,"""
            SELECT r.*,i.serialized,i.name,i.model,i.category,w.technician_id AS warehouse_technician
            FROM stock_reservations r JOIN stock_items i ON i.id=r.item_id
            JOIN stock_warehouses w ON w.id=r.warehouse_id WHERE r.id=@id
            """,P("id",id));
        var r=rows.Single();var o=orders[0];
        if(r["state"] as string!="reserved")return Conflict("Reservation is already resolved");
        if(issue&&(o["state"] as string!="in_progress"||
            (r["warehouse_technician"] is Guid tech&&!Equals(tech,o["technician_id"]))))
            return Conflict("Issue requires an in-progress installation and matching technician stock");
        var item=(Guid)r["item_id"]!;var warehouse=(Guid)r["warehouse_id"]!;var qty=(int)r["quantity"]!;
        var units=await Query(c,t,"SELECT id,serial FROM stock_serials WHERE reservation_id=@id ORDER BY serial",P("id",id));
        if(units.Count!=((bool)r["serialized"]!?qty:0))return Conflict("Reservation serial count mismatch");
        var serials=units.Select(x=>(string)x["serial"]!).ToArray();
        if(issue)
        {
            // Refuse a known pre-existing equipment serial instead of silently
            // duplicating an asset. Existing manually managed assets remain separate.
            if(serials.Length>0&&(await Query(c,t,"SELECT id FROM equipment WHERE recovered_at IS NULL AND upper(serial_number)=ANY(@s) LIMIT 1",P("s",serials))).Count!=0)
                return Conflict("An equipment asset already has one of these serial numbers");
            foreach(var unit in units)
            {
                var equipment=Guid.NewGuid();
                await NetworkManagement.Execute(c,t,"""
                    INSERT INTO equipment(id,customer_id,category,name,model,serial_number,ownership)
                    VALUES(@id,@customer,@cat,@name,@model,@serial,'')
                    """,P("id",equipment),P("customer",o["customer_id"]),P("cat",r["category"]),P("name",r["name"]),
                    P("model",r["model"]),P("serial",unit["serial"]));
                await NetworkManagement.Execute(c,t,"UPDATE stock_serials SET equipment_id=@e,warehouse_id=NULL,reservation_id=NULL WHERE id=@id",
                    P("e",equipment),P("id",unit["id"]));
            }
            await NetworkManagement.Execute(c,t,"UPDATE stock_balances SET reserved=reserved-@q,on_hand=on_hand-@q WHERE warehouse_id=@w AND item_id=@i",
                P("q",qty),P("w",warehouse),P("i",item));
        }
        else
        {
            await NetworkManagement.Execute(c,t,"UPDATE stock_serials SET reservation_id=NULL WHERE reservation_id=@id",P("id",id));
            await NetworkManagement.Execute(c,t,"UPDATE stock_balances SET reserved=reserved-@q WHERE warehouse_id=@w AND item_id=@i",
                P("q",qty),P("w",warehouse),P("i",item));
        }
        await NetworkManagement.Execute(c,t,"UPDATE stock_reservations SET state=@state,resolved_at=now() WHERE id=@id",
            P("state",issue?"issued":"released"),P("id",id));
        await Movement(c,t,ctx,input.RequestId,issue?"issue":"release",item,warehouse,null,id,qty,serials,reference);
        await t.CommitAsync();return Results.Ok(new{ok=true,state=issue?"issued":"released",assetsCreated=issue?units.Count:0});
    }
}
