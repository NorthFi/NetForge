// root/selfhost/src/NetForge.Api/Procurement.cs
// Purchase order approval and receipt use one database transaction, including
// stock quantities, serials and immutable movement history. No GL postings.
using System.Security.Claims;
using Npgsql;

namespace NetForge.Api;
public sealed record SupplierInput(string Name,string? AccountReference,string? Email,string? Phone);
public sealed record SupplierAccess(bool Enabled,bool Confirm);
public sealed record PurchaseLineInput(Guid ItemId,int Quantity,decimal UnitCost);
public sealed record PurchaseOrderInput(Guid SupplierId,Guid WarehouseId,string Reference,DateOnly? ExpectedDay,
    PurchaseLineInput[] Lines,bool Confirm);
public sealed record PurchaseAction(bool Confirm);
public sealed record PurchaseReceive(Guid RequestId,Guid LineId,int Quantity,string[]? Serials,string DeliveryReference,bool Confirm);
public sealed record HighsiteStockInput(Guid RequestId,Guid HighsiteId,Guid WarehouseId,Guid ItemId,
    int Quantity,string[]? Serials,string Reference,bool Confirm);

public static class Procurement
{
    private static NpgsqlParameter P(string k,object? v)=>Store.P(k,v);
    private static bool Admin(HttpContext c)=>c.User.IsInRole("admin");
    private static bool Staff(HttpContext c)=>Admin(c)||c.User.IsInRole("dispatcher");
    private static Guid User(HttpContext c)=>Guid.Parse(c.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private static string Actor(HttpContext c)=>c.User.Identity?.Name??"unknown";
    private static string Clean(string? s)=>(s??"").Trim();
    private static IResult Denied()=>Results.StatusCode(403);
    private static IResult Invalid(string error)=>Results.BadRequest(new{error});
    private static IResult Conflict(string error)=>Results.Conflict(new{error});
    private static Task<List<Dictionary<string,object?>>> Q(NpgsqlConnection c,NpgsqlTransaction t,string sql,params NpgsqlParameter[] p)=>
        NetworkManagement.Query(c,t,sql,p);
    private static Task<List<Dictionary<string,object?>>> Order(NpgsqlConnection c,NpgsqlTransaction t,Guid id)=>
        Q(c,t,"SELECT * FROM procurement_orders WHERE id=@id FOR UPDATE",P("id",id));
    private static Task Audit(NpgsqlConnection c,NpgsqlTransaction t,HttpContext ctx,string action,string entity,Guid id)=>
        NetworkManagement.Audit(c,t,Actor(ctx),action,entity,id.ToString());

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/procurement/suppliers",async(HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"SELECT * FROM procurement_suppliers ORDER BY name LIMIT 2000"));
        });
        api.MapPost("/procurement/suppliers",async(HttpContext ctx,NpgsqlDataSource db,SupplierInput input)=>
        {
            if(!Admin(ctx))return Denied();
            var name=Clean(input.Name);var account=Clean(input.AccountReference);var email=Clean(input.Email);var phone=Clean(input.Phone);
            if(name.Length is <1 or >160||account.Length>100||email.Length>254||phone.Length>70)return Invalid("Invalid supplier fields");
            await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
            var rows=await Q(c,t,"""
                INSERT INTO procurement_suppliers(name,account_reference,email,phone)
                VALUES(@name,@account,@email,@phone) RETURNING id
                """,P("name",name),P("account",account),P("email",email),P("phone",phone));
            var id=(Guid)rows[0]["id"]!;
            await Audit(c,t,ctx,"supplier-create","supplier",id);
            await t.CommitAsync();return Results.Created("/api/procurement/suppliers/"+id,new{id});
        });
        api.MapPut("/procurement/suppliers/{id:guid}/enabled",async(Guid id,HttpContext ctx,NpgsqlDataSource db,SupplierAccess input)=>
        {
            if(!Admin(ctx))return Denied();
            if(!input.Confirm)return Invalid("Confirm supplier status");
            await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
            var rows=await Q(c,t,"UPDATE procurement_suppliers SET enabled=@enabled WHERE id=@id RETURNING id",P("enabled",input.Enabled),P("id",id));
            if(rows.Count!=1)return Results.NotFound();
            await Audit(c,t,ctx,"supplier-status","supplier",id);
            await t.CommitAsync();return Results.Ok(new{ok=true});
        });
        api.MapGet("/procurement/orders",async(HttpContext ctx,NpgsqlDataSource db,string? state,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000||state is not (null or "pending" or "approved" or "partial" or "received" or "cancelled"))return Invalid("Invalid filter");
            return Results.Ok(await Store.Rows(db,"""
                SELECT o.*,w.name AS warehouse,b.name AS branch,u.username AS creator,a.username AS approver,
                    totals.ordered_value,totals.received_value,totals.outstanding
                FROM procurement_orders o JOIN stock_warehouses w ON w.id=o.warehouse_id JOIN branches b ON b.id=w.branch_id
                JOIN app_users u ON u.id=o.created_by LEFT JOIN app_users a ON a.id=o.approved_by
                LEFT JOIN LATERAL(SELECT sum(ordered_value) AS ordered_value,sum(received_value) AS received_value,
                    sum(outstanding) AS outstanding FROM procurement_line_status WHERE order_id=o.id) totals ON true
                WHERE (@state::text IS NULL OR o.state=@state)
                ORDER BY o.created_at DESC,o.id OFFSET @offset LIMIT 100
                """,P("state",state),P("offset",offset??0)));
        });
        api.MapPost("/procurement/orders",Create);
        api.MapGet("/procurement/orders/{id:guid}/lines",async(Guid id,HttpContext ctx,NpgsqlDataSource db)=>
        {
            if(!Staff(ctx))return Denied();
            return Results.Ok(await Store.Rows(db,"SELECT * FROM procurement_line_status WHERE order_id=@id ORDER BY sku",P("id",id)));
        });
        api.MapGet("/procurement/orders/{id:guid}/receipts",async(Guid id,HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT r.request_id,r.delivery_reference,r.received_at,l.sku,m.quantity,m.serials,u.username,
                    l.unit_cost,m.quantity*l.unit_cost AS received_value
                FROM procurement_receipts r JOIN procurement_lines l ON l.id=r.line_id
                JOIN stock_movements m ON m.request_id=r.request_id JOIN app_users u ON u.id=m.actor_id
                WHERE l.order_id=@id ORDER BY r.received_at DESC,r.request_id OFFSET @offset LIMIT 100
                """,P("id",id),P("offset",offset??0)));
        });
        api.MapPost("/procurement/orders/{id:guid}/approve",(Guid id,HttpContext ctx,NpgsqlDataSource db,PurchaseAction input)=>Transition(id,ctx,db,input,true));
        api.MapPost("/procurement/orders/{id:guid}/cancel",(Guid id,HttpContext ctx,NpgsqlDataSource db,PurchaseAction input)=>Transition(id,ctx,db,input,false));
        api.MapPost("/procurement/orders/{id:guid}/receive",Receive);
        api.MapGet("/procurement/reorder",async(HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            // Only existing stock balance rows. Pending/unapproved and cancelled
            // quantities are not considered incoming supply.
            return Results.Ok(await Store.Rows(db,"""
                SELECT b.warehouse_id,b.item_id,w.name AS warehouse,i.sku,i.name,i.min_available,
                    b.on_hand-b.reserved AS available,COALESCE(p.incoming,0) AS incoming,
                    GREATEST(0,i.min_available-(b.on_hand-b.reserved)-COALESCE(p.incoming,0)) AS suggested
                FROM stock_balances b JOIN stock_warehouses w ON w.id=b.warehouse_id
                JOIN stock_items i ON i.id=b.item_id
                LEFT JOIN LATERAL(SELECT sum(l.outstanding) AS incoming
                    FROM procurement_line_status l JOIN procurement_orders o ON o.id=l.order_id
                    WHERE o.warehouse_id=b.warehouse_id AND l.item_id=b.item_id AND o.state IN ('approved','partial')) p ON true
                WHERE w.technician_id IS NULL AND b.on_hand-b.reserved<i.min_available
                ORDER BY w.name,i.sku,b.warehouse_id OFFSET @offset LIMIT 100
                """,P("offset",offset??0)));
        });
        api.MapGet("/procurement/highsite-deployments",async(HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT d.request_id,d.highsite_id,h.name AS highsite,d.deployed_at,i.sku,w.name AS warehouse,
                    m.quantity,m.serials,m.reference,u.username,
                    ARRAY(SELECT a.equipment_id FROM stock_highsite_assets a WHERE a.request_id=d.request_id ORDER BY a.equipment_id) AS equipment_ids
                FROM stock_highsite_deployments d JOIN stock_movements m ON m.request_id=d.request_id
                JOIN stock_items i ON i.id=m.item_id JOIN highsites h ON h.id=d.highsite_id
                JOIN stock_warehouses w ON w.id=m.from_warehouse JOIN app_users u ON u.id=m.actor_id
                ORDER BY d.deployed_at DESC,d.request_id OFFSET @offset LIMIT 100
                """,P("offset",offset??0)));
        });
        api.MapPost("/procurement/highsite-deployments",Deploy);
    }
    private static async Task<IResult> Create(HttpContext ctx,NpgsqlDataSource db,PurchaseOrderInput input)
    {
        if(!Staff(ctx))return Denied();
        var reference=Clean(input.Reference);
        if(!input.Confirm||reference.Length is <3 or >240||input.Lines is not {Length:>=1 and <=50}||
            input.Lines.Any(x=>x is null||x.ItemId==Guid.Empty||x.Quantity is <1 or >100000||x.UnitCost<0||
                x.UnitCost>9999999999.99m||decimal.Round(x.UnitCost,2)!=x.UnitCost)||
            input.Lines.Select(x=>x.ItemId).Distinct().Count()!=input.Lines.Length||
            (input.ExpectedDay.HasValue&&input.ExpectedDay.Value.Year is <2000 or >2100))
            return Invalid("Confirm supplier, warehouse, reference and 1–50 unique valid stock lines");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        var supplier=await Q(c,t,"SELECT name FROM procurement_suppliers WHERE id=@id AND enabled FOR SHARE",P("id",input.SupplierId));
        if(supplier.Count!=1)return Conflict("Enabled supplier required");
        if((await Q(c,t,"SELECT id FROM stock_warehouses WHERE id=@id AND technician_id IS NULL",P("id",input.WarehouseId))).Count!=1)
            return Conflict("Purchase orders must receive into a branch warehouse");
        var id=Guid.NewGuid();
        await NetworkManagement.Execute(c,t,"""
            INSERT INTO procurement_orders(id,supplier_id,supplier_name,warehouse_id,reference,expected_day,created_by)
            VALUES(@id,@supplier,@name,@warehouse,@ref,@day,@actor)
            """,P("id",id),P("supplier",input.SupplierId),P("name",supplier[0]["name"]),P("warehouse",input.WarehouseId),
            P("ref",reference),P("day",input.ExpectedDay),P("actor",User(ctx)));
        foreach(var line in input.Lines)
        {
            var items=await Q(c,t,"SELECT sku,name FROM stock_items WHERE id=@id",P("id",line.ItemId));
            if(items.Count!=1)return Conflict("Unknown stock item");
            await NetworkManagement.Execute(c,t,"""
                INSERT INTO procurement_lines(order_id,item_id,sku,item_name,quantity,unit_cost)
                VALUES(@order,@item,@sku,@name,@qty,@cost)
                """,P("order",id),P("item",line.ItemId),P("sku",items[0]["sku"]),P("name",items[0]["name"]),
                P("qty",line.Quantity),P("cost",line.UnitCost));
        }
        await Audit(c,t,ctx,"purchase-create","purchase_order",id);
        await t.CommitAsync();return Results.Created("/api/procurement/orders/"+id,new{id});
    }
    private static async Task<IResult> Transition(Guid id,HttpContext ctx,NpgsqlDataSource db,PurchaseAction input,bool approve)
    {
        if(!Admin(ctx))return Denied();
        if(!input.Confirm)return Invalid("Confirm purchase order decision");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        var rows=await Order(c,t,id);if(rows.Count!=1)return Results.NotFound();
        var order=rows[0];var state=order["state"] as string;
        if(approve)
        {
            if(state!="pending"||Equals(order["created_by"],User(ctx)))return Conflict("A different administrator must approve a pending order");
            if((await Q(c,t,"SELECT id FROM procurement_suppliers WHERE id=@id AND enabled FOR SHARE",P("id",order["supplier_id"]))).Count!=1)
                return Conflict("Supplier is disabled");
            await NetworkManagement.Execute(c,t,"UPDATE procurement_orders SET state='approved',approved_by=@actor,approved_at=now() WHERE id=@id",
                P("id",id),P("actor",User(ctx)));
        }
        else
        {
            if(state is not ("pending" or "approved" or "partial"))return Conflict("Order is already terminal");
            await NetworkManagement.Execute(c,t,"UPDATE procurement_orders SET state='cancelled' WHERE id=@id",P("id",id));
        }
        await Audit(c,t,ctx,approve?"purchase-approve":"purchase-cancel","purchase_order",id);
        await t.CommitAsync();return Results.Ok(new{state=approve?"approved":"cancelled"});
    }
    private static async Task<IResult> Receive(Guid id,HttpContext ctx,NpgsqlDataSource db,PurchaseReceive input)
    {
        if(!Staff(ctx))return Denied();
        var reference=Clean(input.DeliveryReference);var serials=Warehouse.Serials(input.Serials);
        if(!input.Confirm||input.RequestId==Guid.Empty||input.Quantity is <1 or >100000||reference.Length is <3 or >180)
            return Invalid("Confirm receipt quantity, request ID and delivery reference");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        // Purchase order lock first; stock operations never acquire PO locks.
        // This serializes partial receipts and cancellation without lock cycles.
        var orders=await Order(c,t,id);if(orders.Count!=1)return Results.NotFound();
        var order=orders[0];
        if(order["state"] as string is not ("approved" or "partial"))return Conflict("Purchase order is not open for receiving");
        await Warehouse.Lock(c,t);
        if(await Warehouse.Replayed(c,t,input.RequestId))return Conflict("Receipt request already recorded; inspect receipt history");
        var lines=await Q(c,t,"SELECT * FROM procurement_line_status WHERE id=@line AND order_id=@order",P("line",input.LineId),P("order",id));
        if(lines.Count!=1)return Results.NotFound();
        var line=lines[0];
        if(input.Quantity>(int)line["outstanding"]!)return Conflict("Receipt exceeds unreceived order quantity");
        if(!Warehouse.ValidSerials((bool)line["serialized"]!,input.Quantity,serials))
            return Invalid("Serialized receipt requires explicit unique serials; bulk receipt omits serials");
        await Warehouse.ReceiveLocked(c,t,ctx,input.RequestId,(Guid)line["item_id"]!,(Guid)order["warehouse_id"]!,
            input.Quantity,serials,"PO-"+order["number"]+" / "+reference);
        await NetworkManagement.Execute(c,t,"INSERT INTO procurement_receipts(request_id,line_id,delivery_reference) VALUES(@id,@line,@ref)",
            P("id",input.RequestId),P("line",input.LineId),P("ref",reference));
        var open=await Q(c,t,"SELECT id FROM procurement_line_status WHERE order_id=@id AND outstanding>0 LIMIT 1",P("id",id));
        var state=open.Count==0?"received":"partial";
        await NetworkManagement.Execute(c,t,"UPDATE procurement_orders SET state=@state WHERE id=@id",P("id",id),P("state",state));
        await Audit(c,t,ctx,"purchase-receive","purchase_order",id);
        await t.CommitAsync();return Results.Ok(new{state,requestId=input.RequestId});
    }
    private static async Task<IResult> Deploy(HttpContext ctx,NpgsqlDataSource db,HighsiteStockInput input)
    {
        if(!Staff(ctx))return Denied();
        var reference=Clean(input.Reference);var serials=Warehouse.Serials(input.Serials);
        if(!input.Confirm||input.RequestId==Guid.Empty||input.Quantity is <1 or >100000||reference.Length is <3 or >240)
            return Invalid("Confirm physical deployment, quantity, reference and request ID");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        await Warehouse.Lock(c,t);
        if(await Warehouse.Replayed(c,t,input.RequestId))return Conflict("Deployment request already recorded");
        var sites=await Q(c,t,"SELECT branch_id FROM highsites WHERE id=@id FOR SHARE",P("id",input.HighsiteId));
        var warehouses=await Q(c,t,"SELECT branch_id FROM stock_warehouses WHERE id=@id",P("id",input.WarehouseId));
        var items=await Q(c,t,"SELECT * FROM stock_items WHERE id=@id",P("id",input.ItemId));
        if(sites.Count!=1||warehouses.Count!=1||items.Count!=1)return Results.NotFound();
        if(sites[0]["branch_id"] is not Guid branch||!Equals(branch,warehouses[0]["branch_id"]))
            return Conflict("Highsite and stock warehouse must belong to the same branch");
        var item=items[0];
        if(!Warehouse.ValidSerials((bool)item["serialized"]!,input.Quantity,serials))return Invalid("Invalid serial selection");
        if(!await Warehouse.HasAvailable(c,t,input.WarehouseId,input.ItemId,input.Quantity)||
           !await Warehouse.AvailableSerials(c,t,input.WarehouseId,input.ItemId,serials))return Conflict("Insufficient available stock or unavailable serials");
        if(serials.Length>0&&(await Q(c,t,"SELECT id FROM equipment WHERE upper(serial_number)=ANY(@s) LIMIT 1",P("s",serials))).Count!=0)
            return Conflict("An existing equipment asset has one of these serials");
        await NetworkManagement.Execute(c,t,"UPDATE stock_balances SET on_hand=on_hand-@qty WHERE warehouse_id=@w AND item_id=@i",
            P("qty",input.Quantity),P("w",input.WarehouseId),P("i",input.ItemId));
        // Same issue movement type as installations, so reconciliation
        // automatically includes highsite deployment deductions.
        await Warehouse.Movement(c,t,ctx,input.RequestId,"issue",input.ItemId,input.WarehouseId,null,null,input.Quantity,serials,reference);
        await NetworkManagement.Execute(c,t,"INSERT INTO stock_highsite_deployments(request_id,highsite_id) VALUES(@id,@site)",
            P("id",input.RequestId),P("site",input.HighsiteId));
        foreach(var serial in serials)
        {
            var equipment=Guid.NewGuid();
            await NetworkManagement.Execute(c,t,"""
                INSERT INTO equipment(id,highsite_id,category,name,model,serial_number,ownership)
                VALUES(@id,@site,@category,@name,@model,@serial,'')
                """,P("id",equipment),P("site",input.HighsiteId),P("category",item["category"]),P("name",item["name"]),
                P("model",item["model"]),P("serial",serial));
            await NetworkManagement.Execute(c,t,"""
                UPDATE stock_serials SET warehouse_id=NULL,equipment_id=@equipment WHERE item_id=@item AND serial=@serial
                """,P("equipment",equipment),P("item",input.ItemId),P("serial",serial));
            await NetworkManagement.Execute(c,t,"INSERT INTO stock_highsite_assets(request_id,equipment_id) VALUES(@id,@equipment)",
                P("id",input.RequestId),P("equipment",equipment));
        }
        await Audit(c,t,ctx,"highsite-stock-deploy","highsite",input.HighsiteId);
        await t.CommitAsync();return Results.Ok(new{requestId=input.RequestId,assetsCreated=serials.Length});
    }
}
