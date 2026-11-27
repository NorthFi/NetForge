// root/selfhost/src/NetForge.Api/EquipmentLifecycle.cs
// Recovery never counts quarantine as saleable stock. Retain the original
// deployment record and serial identity, and require an independent release.
using System.Security.Claims;
using Npgsql;

namespace NetForge.Api;
public sealed record RecoveryInput(Guid RequestId,Guid EquipmentId,Guid ItemId,Guid WarehouseId,
    DateOnly? WarrantyUntil,string Note,bool Confirm);
public sealed record RecoveryAction(Guid RequestId,string Action,string Note,bool Confirm,
    Guid? SupplierId=null,string? RmaReference=null,string? Condition=null);

public static class EquipmentLifecycle
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
    private static async Task<bool> Replayed(NpgsqlConnection c,NpgsqlTransaction t,Guid request)=>
        await Warehouse.Replayed(c,t,request)||(await Q(c,t,"SELECT id FROM equipment_lifecycle_events WHERE request_id=@id",P("id",request))).Count!=0;
    private static async Task Event(NpgsqlConnection c,NpgsqlTransaction t,HttpContext ctx,Guid id,Guid request,string action,
        string note,Guid? supplier=null,string? reference=null)
    {
        await NetworkManagement.Execute(c,t,"""
            INSERT INTO equipment_lifecycle_events(request_id,recovery_id,action,note,supplier_id,rma_reference,actor_id)
            VALUES(@request,@id,@action,@note,@supplier,@ref,@actor)
            """,P("request",request),P("id",id),P("action",action),P("note",note),P("supplier",supplier),
            P("ref",reference),P("actor",User(ctx)));
        await NetworkManagement.Audit(c,t,Actor(ctx),"equipment-"+action,"equipment_recovery",id.ToString());
    }
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/lifecycle/eligible",async(HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT e.id,e.name,e.model,e.serial_number,e.category,c.account,h.name AS highsite,
                    COALESCE(c.branch_id,h.branch_id) AS branch_id,s.item_id,
                    (EXISTS(SELECT 1 FROM monitor_targets m WHERE m.equipment_id=e.id)
                    OR EXISTS(SELECT 1 FROM snmp_devices m WHERE m.equipment_id=e.id)
                    OR EXISTS(SELECT 1 FROM assurance_cpe_probes m WHERE m.equipment_id=e.id)
                    OR EXISTS(SELECT 1 FROM routeros_devices m WHERE m.equipment_id=e.id)
                    OR EXISTS(SELECT 1 FROM backup_profiles m WHERE m.equipment_id=e.id)) AS has_dependencies
                FROM equipment e LEFT JOIN customers c ON c.id=e.customer_id
                LEFT JOIN highsites h ON h.id=e.highsite_id LEFT JOIN stock_serials s ON s.equipment_id=e.id
                WHERE e.recovered_at IS NULL AND e.serial_number<>'' ORDER BY e.created_at DESC,e.id OFFSET @offset LIMIT 100
                """,P("offset",offset??0)));
        });
        api.MapGet("/lifecycle/recoveries",async(HttpContext ctx,NpgsqlDataSource db,string? state,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000||state is not (null or "quarantine" or "passed" or "failed" or "rma" or "released" or "scrapped"))
                return Invalid("Invalid filters");
            return Results.Ok(await Store.Rows(db,"""
                SELECT r.*,s.serial,i.sku,i.name AS item,w.name AS warehouse,c.account,h.name AS highsite,
                    p.name AS supplier,u.username AS tester
                FROM equipment_recoveries r JOIN stock_serials s ON s.id=r.serial_id
                JOIN stock_items i ON i.id=s.item_id JOIN stock_warehouses w ON w.id=r.warehouse_id
                JOIN equipment e ON e.id=r.equipment_id LEFT JOIN customers c ON c.id=e.customer_id
                LEFT JOIN highsites h ON h.id=e.highsite_id LEFT JOIN procurement_suppliers p ON p.id=r.supplier_id
                LEFT JOIN app_users u ON u.id=r.last_test_by
                WHERE (@state::text IS NULL OR r.state=@state) ORDER BY r.created_at DESC,r.id OFFSET @offset LIMIT 100
                """,P("state",state),P("offset",offset??0)));
        });
        api.MapGet("/lifecycle/recoveries/{id:guid}/events",async(Guid id,HttpContext ctx,NpgsqlDataSource db,int? offset)=>
        {
            if(!Staff(ctx))return Denied();
            if(offset is <0 or >1000000)return Invalid("Invalid offset");
            return Results.Ok(await Store.Rows(db,"""
                SELECT e.*,u.username,p.name AS supplier FROM equipment_lifecycle_events e
                JOIN app_users u ON u.id=e.actor_id LEFT JOIN procurement_suppliers p ON p.id=e.supplier_id
                WHERE e.recovery_id=@id ORDER BY e.id DESC OFFSET @offset LIMIT 100
                """,P("id",id),P("offset",offset??0)));
        });
        api.MapPost("/lifecycle/recoveries",Recover);
        api.MapPost("/lifecycle/recoveries/{id:guid}/action",Act);
    }
    private static async Task<IResult> Recover(HttpContext ctx,NpgsqlDataSource db,RecoveryInput input)
    {
        if(!Staff(ctx))return Denied();
        var note=Clean(input.Note);
        if(!input.Confirm||input.RequestId==Guid.Empty||note.Length is <5 or >1000||
            (input.WarrantyUntil.HasValue&&input.WarrantyUntil.Value.Year is <2000 or >2100))
            return Invalid("Confirm actual physical recovery, reference note and optional warranty date");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        await Warehouse.Lock(c,t);
        if(await Replayed(c,t,input.RequestId))return Conflict("Request already recorded");
        var assets=await Q(c,t,"""
            SELECT e.*,COALESCE(c.branch_id,h.branch_id) AS branch_id
            FROM equipment e LEFT JOIN customers c ON c.id=e.customer_id LEFT JOIN highsites h ON h.id=e.highsite_id
            WHERE e.id=@id FOR UPDATE OF e
            """,P("id",input.EquipmentId));
        if(assets.Count!=1)return Results.NotFound();
        var asset=assets[0];
        if(asset["recovered_at"] is not null)return Conflict("This deployment is already historical");
        // Deliberately block even disabled profiles, preserving historical
        // references and preventing background jobs from targeting a new owner.
        var dependencies=await Q(c,t,"""
            SELECT 'TCP monitor' AS kind FROM monitor_targets WHERE equipment_id=@id
            UNION ALL SELECT 'SNMP' FROM snmp_devices WHERE equipment_id=@id
            UNION ALL SELECT 'CPE probe' FROM assurance_cpe_probes WHERE equipment_id=@id
            UNION ALL SELECT 'RouterOS inventory' FROM routeros_devices WHERE equipment_id=@id
            UNION ALL SELECT 'backup vault' FROM backup_profiles WHERE equipment_id=@id
            """,P("id",input.EquipmentId));
        if(dependencies.Count>0)return Conflict("Recovery blocked by existing operational profiles: "+string.Join(", ",dependencies.Select(x=>x["kind"]).Distinct()));
        var warehouses=await Q(c,t,"SELECT branch_id,technician_id FROM stock_warehouses WHERE id=@id",P("id",input.WarehouseId));
        if(warehouses.Count!=1)return Results.NotFound();
        if(asset["branch_id"] is not Guid branch||!Equals(branch,warehouses[0]["branch_id"])||warehouses[0]["technician_id"] is not null)
            return Conflict("Recover into a branch warehouse belonging to the original asset branch, not a technician warehouse");
        var items=await Q(c,t,"SELECT serialized,category,model FROM stock_items WHERE id=@id",P("id",input.ItemId));
        if(items.Count!=1)return Results.NotFound();
        var serial=Clean(asset["serial_number"] as string).ToUpperInvariant();
        if(items[0]["serialized"] is not true||!Warehouse.ValidSerials(true,1,[serial])||
            !Equals(items[0]["category"],asset["category"]))
            return Invalid("Select a matching serialized catalogue item and valid asset serial");
        if((await Q(c,t,"SELECT id FROM equipment WHERE id<>@id AND recovered_at IS NULL AND upper(serial_number)=@s LIMIT 1",
            P("id",input.EquipmentId),P("s",serial))).Count!=0)return Conflict("Duplicate active equipment serial; reconcile first");
        var units=await Q(c,t,"""
            SELECT id,item_id,serial,equipment_id,warehouse_id,reservation_id FROM stock_serials
            WHERE equipment_id=@asset OR (item_id=@item AND serial=@serial)
            """,P("asset",input.EquipmentId),P("item",input.ItemId),P("serial",serial));
        Guid serialId;
        if(units.Count==0)
        {
            // Legacy manually captured equipment: introduce a physical serial
            // in quarantine, not a receipt or a stock balance increase.
            if(!Equals(items[0]["model"],asset["model"]))return Conflict("Legacy asset model must match catalogue model exactly");
            serialId=Guid.NewGuid();
            await NetworkManagement.Execute(c,t,"INSERT INTO stock_serials(id,item_id,serial,equipment_id) VALUES(@id,@item,@serial,@asset)",
                P("id",serialId),P("item",input.ItemId),P("serial",serial),P("asset",input.EquipmentId));
        }
        else
        {
            if(units.Count!=1||!Equals(units[0]["item_id"],input.ItemId)||!Equals(units[0]["serial"],serial)||
                !Equals(units[0]["equipment_id"],input.EquipmentId)||units[0]["warehouse_id"] is not null||units[0]["reservation_id"] is not null)
                return Conflict("Serial is already associated with a different inventory record");
            serialId=(Guid)units[0]["id"]!;
        }
        var id=input.RequestId;
        await NetworkManagement.Execute(c,t,"UPDATE equipment SET recovered_at=now(),management_ip=NULL WHERE id=@id",P("id",input.EquipmentId));
        await NetworkManagement.Execute(c,t,"""
            INSERT INTO equipment_recoveries(id,equipment_id,serial_id,warehouse_id,warranty_until,created_by)
            VALUES(@id,@asset,@serial,@warehouse,@warranty,@actor)
            """,P("id",id),P("asset",input.EquipmentId),P("serial",serialId),P("warehouse",input.WarehouseId),
            P("warranty",input.WarrantyUntil),P("actor",User(ctx)));
        await Event(c,t,ctx,id,input.RequestId,"recover",note);
        await t.CommitAsync();return Results.Created("/api/lifecycle/recoveries/"+id,new{id,state="quarantine"});
    }
    private static async Task<IResult> Act(Guid id,HttpContext ctx,NpgsqlDataSource db,RecoveryAction input)
    {
        if(!Staff(ctx))return Denied();
        var action=input.Action;var note=Clean(input.Note);var reference=Clean(input.RmaReference);
        if(!input.Confirm||input.RequestId==Guid.Empty||note.Length is <5 or >1000||
            action is not ("test_pass" or "test_fail" or "send_rma" or "return_rma" or "release" or "scrap"))
            return Invalid("Confirm lifecycle action with inspection or custody evidence");
        if(action is ("release" or "scrap")&&!Admin(ctx))return Denied();
        if(action=="test_pass"&&input.Condition is not ("tested_used" or "refurbished"))return Invalid("Select tested used or refurbished condition");
        if(action=="send_rma"&&(input.SupplierId is null||reference.Length is <3 or >160))return Invalid("Supplier and RMA reference required");
        await using var c=await db.OpenConnectionAsync();await using var t=await c.BeginTransactionAsync();
        await Warehouse.Lock(c,t);
        if(await Replayed(c,t,input.RequestId))return Conflict("Request already recorded");
        var rows=await Q(c,t,"""
            SELECT r.*,s.item_id,s.serial,s.equipment_id AS assigned_asset,s.warehouse_id AS stock_warehouse
            FROM equipment_recoveries r JOIN stock_serials s ON s.id=r.serial_id
            WHERE r.id=@id FOR UPDATE OF r,s
            """,P("id",id));
        if(rows.Count!=1)return Results.NotFound();
        var r=rows[0];var old=(string)r["state"]!;
        if(old is "released" or "scrapped")return Conflict("Recovery is already resolved");
        if(!Equals(r["assigned_asset"],r["equipment_id"])||r["stock_warehouse"] is not null)
            return Conflict("Physical serial custody mismatch");
        var next=(old,action) switch {
            ("quarantine" or "failed" or "passed","test_pass")=>"passed",
            ("quarantine" or "failed" or "passed","test_fail")=>"failed",
            ("failed","send_rma")=>"rma",
            ("rma","return_rma")=>"quarantine",
            ("passed","release")=>"released",
            ("failed","scrap")=>"scrapped",
            _=>null
        };
        if(next is null)return Conflict("Lifecycle transition is not permitted");
        if(action is ("release" or "scrap")&&Equals(r["last_test_by"],User(ctx)))
            return Conflict("A different administrator must confirm release or disposal after testing");
        Guid? supplier=null;string? rma=null;
        if(action=="send_rma")
        {
            if((await Q(c,t,"SELECT id FROM procurement_suppliers WHERE id=@id AND enabled FOR SHARE",P("id",input.SupplierId))).Count!=1)
                return Conflict("Enabled supplier required");
            supplier=input.SupplierId;rma=reference;
            await NetworkManagement.Execute(c,t,"UPDATE equipment_recoveries SET supplier_id=@supplier,rma_reference=@ref WHERE id=@id",
                P("supplier",supplier),P("ref",rma),P("id",id));
        }
        if(action=="return_rma"){supplier=r["supplier_id"] as Guid?;rma=r["rma_reference"] as string;}
        if(action is "test_pass" or "test_fail" or "return_rma")
            await NetworkManagement.Execute(c,t,"UPDATE equipment_recoveries SET last_test_by=@tester,condition=@condition WHERE id=@id",
                P("tester",action=="return_rma"?null:User(ctx)),P("condition",action=="test_pass"?input.Condition:null),P("id",id));
        if(action=="release")
        {
            // Receipt of recovered stock uses the existing ledger convention.
            // Do not insert another stock_serial, and never revive the old asset.
            var item=(Guid)r["item_id"]!;var warehouse=(Guid)r["warehouse_id"]!;
            await Warehouse.Credit(c,t,warehouse,item,1);
            await NetworkManagement.Execute(c,t,"""
                UPDATE stock_serials SET equipment_id=NULL,warehouse_id=@w,condition=@condition WHERE id=@serial
                """,P("w",warehouse),P("condition",r["condition"]),P("serial",r["serial_id"]));
            await Warehouse.Movement(c,t,ctx,input.RequestId,"receive",item,null,warehouse,null,1,
                [(string)r["serial"]!],"Recovered equipment / "+id);
        }
        await NetworkManagement.Execute(c,t,"""
            UPDATE equipment_recoveries SET state=@state,resolved_at=CASE WHEN @state IN ('released','scrapped') THEN now() ELSE NULL END
            WHERE id=@id
            """,P("state",next),P("id",id));
        await Event(c,t,ctx,id,input.RequestId,action,note,supplier,rma);
        await t.CommitAsync();return Results.Ok(new{state=next});
    }
}
