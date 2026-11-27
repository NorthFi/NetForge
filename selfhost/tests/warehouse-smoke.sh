#!/usr/bin/env bash
# root/selfhost/tests/warehouse-smoke.sh
# Existing OTHER_INSTALL is unscheduled; use this disposable order.
api POST /stock/items '{"sku":"CI-C6X","name":"Mimosa C6x","model":"C6x","category":"cpe","unit":"each","serialized":true,"minAvailable":2}' 201
ST_ITEM="$(id)"
api POST /stock/items '{"sku":"CI-CABLE","name":"Cable","category":"other","unit":"metre","serialized":false,"minAvailable":10}' 201
ST_BULK="$(id)"
api POST /stock/warehouses "{\"branchId\":\"$BRANCH\",\"name\":\"CI main warehouse\"}" 201
ST_MAIN="$(id)"
api PUT "/installations/roster/$BRANCH" "{\"userId\":\"$INSTALL_TECH\",\"enabled\":true,\"confirm\":true}" 200
api POST /stock/warehouses "{\"branchId\":\"$BRANCH\",\"name\":\"CI technician van\",\"technicianId\":\"$INSTALL_TECH\"}" 201
ST_VAN="$(id)"
req(){ python3 -c 'import uuid;print(uuid.uuid4())'; }
RECEIVE="$(req)"
RECEIPT="{\"requestId\":\"$RECEIVE\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_MAIN\",\"quantity\":2,\"serials\":[\"CI-001\",\"CI-002\"],\"reference\":\"CI receiving\",\"confirm\":true}"
api POST /stock/receive "$RECEIPT" 200
api POST /stock/receive "$RECEIPT" 409
api POST /stock/receive "{\"requestId\":\"$(req)\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_MAIN\",\"quantity\":1,\"serials\":[\"ci-001\"],\"reference\":\"CI duplicate\",\"confirm\":true}" 409
api POST /stock/receive "{\"requestId\":\"$(req)\",\"itemId\":\"$ST_BULK\",\"warehouseId\":\"$ST_MAIN\",\"quantity\":100,\"reference\":\"CI cable\",\"confirm\":true}" 200
api POST /stock/transfer "{\"requestId\":\"$(req)\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_MAIN\",\"destinationId\":\"$ST_VAN\",\"quantity\":1,\"serials\":[\"CI-001\"],\"reference\":\"CI transfer\",\"confirm\":true}" 200
RESERVE="$(req)"
RESERVATION="{\"requestId\":\"$RESERVE\",\"orderId\":\"$OTHER_INSTALL\",\"warehouseId\":\"$ST_VAN\",\"itemId\":\"$ST_ITEM\",\"quantity\":1,\"serials\":[\"CI-001\"],\"reference\":\"CI reservation\",\"confirm\":true}"
api POST /stock/reservations "$RESERVATION" 409
api POST "/installations/orders/$OTHER_INSTALL/schedule" "{\"technicianId\":\"$INSTALL_TECH\",\"day\":\"$DAY\",\"confirm\":true}" 200
api POST /stock/reservations "$RESERVATION" 201
api POST /stock/transfer "{\"requestId\":\"$(req)\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_VAN\",\"destinationId\":\"$ST_MAIN\",\"quantity\":1,\"serials\":[\"CI-001\"],\"reference\":\"CI blocked transfer\",\"confirm\":true}" 409
api POST "/installations/orders/$OTHER_INSTALL/state" '{"state":"cancelled","note":"Stock still reserved","confirm":true}' 409
api POST "/stock/reservations/$RESERVE/issue" "{\"requestId\":\"$(req)\",\"reference\":\"CI premature issue\",\"confirm\":true}" 409
BULK_RES="$(req)"
api POST /stock/reservations "{\"requestId\":\"$BULK_RES\",\"orderId\":\"$OTHER_INSTALL\",\"warehouseId\":\"$ST_MAIN\",\"itemId\":\"$ST_BULK\",\"quantity\":30,\"reference\":\"CI cable reserve\",\"confirm\":true}" 201
api POST /stock/reservations "{\"requestId\":\"$(req)\",\"orderId\":\"$OTHER_INSTALL\",\"warehouseId\":\"$ST_MAIN\",\"itemId\":\"$ST_BULK\",\"quantity\":71,\"reference\":\"CI oversell\",\"confirm\":true}" 409
api POST "/stock/reservations/$BULK_RES/release" "{\"requestId\":\"$(req)\",\"reference\":\"CI unused cable\",\"confirm\":true}" 200
api POST "/installations/orders/$OTHER_INSTALL/state" '{"state":"in_progress","note":"Installation started","confirm":true}' 200
api POST "/installations/orders/$OTHER_INSTALL/state" '{"state":"completed","note":"Stock still reserved","confirm":true}' 409
api POST "/stock/reservations/$RESERVE/issue" "{\"requestId\":\"$(req)\",\"reference\":\"CI equipment installed\",\"confirm\":true}" 200
grep -q '"assetsCreated":1' "$BASE/result.json"
api POST "/stock/reservations/$RESERVE/issue" "{\"requestId\":\"$(req)\",\"reference\":\"CI duplicate issue\",\"confirm\":true}" 409
api POST "/installations/orders/$OTHER_INSTALL/state" '{"state":"completed","note":"Installation verified","confirm":true}' 200
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT on_hand||':'||reserved FROM stock_balances WHERE warehouse_id='$ST_VAN' AND item_id='$ST_ITEM'" | grep -qx '0:0'
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT on_hand||':'||reserved FROM stock_balances WHERE warehouse_id='$ST_MAIN' AND item_id='$ST_BULK'" | grep -qx '100:0'
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT count(*) FROM equipment e JOIN customers c ON c.id=e.customer_id WHERE e.serial_number='CI-001' AND c.account='CI-ONBOARD-OTHER'" | grep -qx 1
api GET /stock/movements "" 200
grep -q '"action":"issue"' "$BASE/result.json"
api GET /stock/reconciliation "" 200
python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))==[]' "$BASE/result.json"
# Simulate an out-of-band quantity discrepancy in the disposable database.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "UPDATE stock_balances SET on_hand=on_hand+1 WHERE warehouse_id='$ST_MAIN' AND item_id='$ST_BULK'" >/dev/null
api GET /stock/reconciliation "" 200
python3 - "$BASE/result.json" "$ST_BULK" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
assert len(r)==1 and r[0]["item_id"]==sys.argv[2] and r[0]["on_hand"]==101 and r[0]["ledger_on_hand"]==100,r
PY
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "UPDATE stock_balances SET on_hand=on_hand-1 WHERE warehouse_id='$ST_MAIN' AND item_id='$ST_BULK'" >/dev/null
api GET /stock/reconciliation "" 200
python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))==[]' "$BASE/result.json"
echo "Warehouse stock smoke passed."
