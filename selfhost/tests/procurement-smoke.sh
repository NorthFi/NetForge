#!/usr/bin/env bash
# root/selfhost/tests/procurement-smoke.sh
api POST /procurement/suppliers '{"name":"CI RF Supplier","accountReference":"CI supplier"}' 201
SUPPLIER="$(id)"
api POST /procurement/orders "{\"supplierId\":\"$SUPPLIER\",\"warehouseId\":\"$ST_MAIN\",\"reference\":\"CI procurement\",\"lines\":[{\"itemId\":\"$ST_ITEM\",\"quantity\":3,\"unitCost\":1250.50},{\"itemId\":\"$ST_BULK\",\"quantity\":50,\"unitCost\":5.25}],\"confirm\":true}" 201
PO="$(id)"
api GET "/procurement/orders/$PO/lines" "" 200
PO_SERIAL_LINE="$(python3 - "$BASE/result.json" "$ST_ITEM" <<'PY'
import json,sys
print(next(r["id"] for r in json.load(open(sys.argv[1])) if r["item_id"]==sys.argv[2]))
PY
)"
PO_BULK_LINE="$(python3 - "$BASE/result.json" "$ST_BULK" <<'PY'
import json,sys
print(next(r["id"] for r in json.load(open(sys.argv[1])) if r["item_id"]==sys.argv[2]))
PY
)"
PO_RECEIPT="$(req)"
PO_PAYLOAD="{\"requestId\":\"$PO_RECEIPT\",\"lineId\":\"$PO_SERIAL_LINE\",\"quantity\":2,\"serials\":[\"CI-PO-01\",\"CI-PO-02\"],\"deliveryReference\":\"CI delivery 1\",\"confirm\":true}"
api POST "/procurement/orders/$PO/receive" "$PO_PAYLOAD" 409
api POST "/procurement/orders/$PO/approve" '{"confirm":true}' 409
PO_ADMIN_COOKIE="$COOKIE"; PO_ADMIN_CSRF="$CSRF"
COOKIE="$APPROVER_COOKIE"; CSRF="$APPROVER_CSRF"
api POST "/procurement/orders/$PO/approve" '{"confirm":true}' 200
COOKIE="$PO_ADMIN_COOKIE"; CSRF="$PO_ADMIN_CSRF"
api POST "/procurement/orders/$PO/receive" "$PO_PAYLOAD" 200
grep -q '"state":"partial"' "$BASE/result.json"
api POST "/procurement/orders/$PO/receive" "$PO_PAYLOAD" 409
api POST "/procurement/orders/$PO/receive" "{\"requestId\":\"$(req)\",\"lineId\":\"$PO_SERIAL_LINE\",\"quantity\":2,\"serials\":[\"CI-PO-03\",\"CI-PO-04\"],\"deliveryReference\":\"CI over receipt\",\"confirm\":true}" 409
api POST "/procurement/orders/$PO/receive" "{\"requestId\":\"$(req)\",\"lineId\":\"$PO_SERIAL_LINE\",\"quantity\":1,\"serials\":[\"CI-PO-01\"],\"deliveryReference\":\"CI duplicate serial\",\"confirm\":true}" 409
api POST "/procurement/orders/$PO/receive" "{\"requestId\":\"$(req)\",\"lineId\":\"$PO_SERIAL_LINE\",\"quantity\":1,\"serials\":[\"CI-PO-03\"],\"deliveryReference\":\"CI delivery 2\",\"confirm\":true}" 200
api POST "/procurement/orders/$PO/receive" "{\"requestId\":\"$(req)\",\"lineId\":\"$PO_BULK_LINE\",\"quantity\":50,\"deliveryReference\":\"CI cable delivery\",\"confirm\":true}" 200
grep -q '"state":"received"' "$BASE/result.json"
api POST "/procurement/orders/$PO/cancel" '{"confirm":true}' 409
api GET "/procurement/orders/$PO/receipts" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)==3 and sum(r["quantity"] for r in rows)==53,rows
PY
# Deploy one newly received serialized unit directly to the existing highsite.
DEPLOY="$(req)"
DEPLOY_PAYLOAD="{\"requestId\":\"$DEPLOY\",\"highsiteId\":\"$SITE\",\"warehouseId\":\"$ST_MAIN\",\"itemId\":\"$ST_ITEM\",\"quantity\":1,\"serials\":[\"CI-PO-01\"],\"reference\":\"CI highsite deployment\",\"confirm\":true}"
api POST /procurement/highsite-deployments "$DEPLOY_PAYLOAD" 200
grep -q '"assetsCreated":1' "$BASE/result.json"
api POST /procurement/highsite-deployments "$DEPLOY_PAYLOAD" 409
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT count(*) FROM equipment WHERE highsite_id='$SITE' AND serial_number='CI-PO-01'" | grep -qx 1
api GET /procurement/highsite-deployments "" 200
grep -q 'CI-PO-01' "$BASE/result.json"
api GET /procurement/reorder "" 200
api GET /stock/reconciliation "" 200
python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))==[]' "$BASE/result.json"
echo "Procurement and highsite stock smoke passed."
