#!/usr/bin/env bash
# root/selfhost/tests/lifecycle-smoke.sh
# Recover the actual highsite asset produced by procurement; retain history.
REC_ASSET="$(docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT id FROM equipment WHERE highsite_id='$SITE' AND serial_number='CI-PO-01'")"
REC_ID="$(req)"
REC_PAYLOAD="{\"requestId\":\"$REC_ID\",\"equipmentId\":\"$REC_ASSET\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_MAIN\",\"warrantyUntil\":\"2028-01-01\",\"note\":\"CI physical recovery\",\"confirm\":true}"
api POST /lifecycle/recoveries "$REC_PAYLOAD" 201
api POST /lifecycle/recoveries "$REC_PAYLOAD" 409
api POST "/lifecycle/recoveries/$REC_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"release\",\"note\":\"CI premature release\",\"confirm\":true}" 409
api POST /procurement/highsite-deployments "{\"requestId\":\"$(req)\",\"highsiteId\":\"$SITE\",\"warehouseId\":\"$ST_MAIN\",\"itemId\":\"$ST_ITEM\",\"quantity\":1,\"serials\":[\"CI-PO-01\"],\"reference\":\"CI quarantined issue\",\"confirm\":true}" 409
api POST "/lifecycle/recoveries/$REC_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"test_fail\",\"note\":\"CI failed RF inspection\",\"confirm\":true}" 200
api PUT "/procurement/suppliers/$SUPPLIER/enabled" '{"enabled":true,"confirm":true}' 200
api POST "/lifecycle/recoveries/$REC_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"send_rma\",\"supplierId\":\"$SUPPLIER\",\"rmaReference\":\"RMA-CI-001\",\"note\":\"CI supplier dispatch\",\"confirm\":true}" 200
api POST "/lifecycle/recoveries/$REC_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"test_pass\",\"condition\":\"refurbished\",\"note\":\"CI invalid remote test\",\"confirm\":true}" 409
api POST "/lifecycle/recoveries/$REC_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"return_rma\",\"note\":\"CI same serial received\",\"confirm\":true}" 200
api POST "/lifecycle/recoveries/$REC_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"test_pass\",\"condition\":\"refurbished\",\"note\":\"CI bench test passed\",\"confirm\":true}" 200
RELEASE_ID="$(req)"
RELEASE_PAYLOAD="{\"requestId\":\"$RELEASE_ID\",\"action\":\"release\",\"note\":\"CI independent release\",\"confirm\":true}"
api POST "/lifecycle/recoveries/$REC_ID/action" "$RELEASE_PAYLOAD" 409
LIFE_COOKIE="$COOKIE"; LIFE_CSRF="$CSRF"
COOKIE="$APPROVER_COOKIE"; CSRF="$APPROVER_CSRF"
api POST "/lifecycle/recoveries/$REC_ID/action" "$RELEASE_PAYLOAD" 200
api POST "/lifecycle/recoveries/$REC_ID/action" "$RELEASE_PAYLOAD" 409
COOKIE="$LIFE_COOKIE"; CSRF="$LIFE_CSRF"
api GET "/lifecycle/recoveries/$REC_ID/events" "" 200
python3 -c 'import json,sys;assert len(json.load(open(sys.argv[1])))==6' "$BASE/result.json"
api POST /procurement/highsite-deployments "{\"requestId\":\"$(req)\",\"highsiteId\":\"$SITE\",\"warehouseId\":\"$ST_MAIN\",\"itemId\":\"$ST_ITEM\",\"quantity\":1,\"serials\":[\"CI-PO-01\"],\"reference\":\"CI redeployment\",\"confirm\":true}" 200
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT count(*) FROM equipment WHERE serial_number='CI-PO-01'" | grep -qx 2
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT count(*) FROM equipment WHERE serial_number='CI-PO-01' AND recovered_at IS NULL" | grep -qx 1
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT count(*) FROM stock_serials WHERE item_id='$ST_ITEM' AND serial='CI-PO-01' AND condition='refurbished'" | grep -qx 1
api GET /stock/reconciliation "" 200
python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))==[]' "$BASE/result.json"
# Customer recovery and authorized disposal do not create stock.
REC_CUST="$(docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT id FROM equipment WHERE serial_number='CI-001' AND recovered_at IS NULL")"
REC_CUST_ID="$(req)"
api POST /lifecycle/recoveries "{\"requestId\":\"$REC_CUST_ID\",\"equipmentId\":\"$REC_CUST\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_MAIN\",\"note\":\"CI customer recovery\",\"confirm\":true}" 201
api POST "/lifecycle/recoveries/$REC_CUST_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"test_fail\",\"note\":\"CI irreparable unit\",\"confirm\":true}" 200
COOKIE="$APPROVER_COOKIE"; CSRF="$APPROVER_CSRF"
api POST "/lifecycle/recoveries/$REC_CUST_ID/action" "{\"requestId\":\"$(req)\",\"action\":\"scrap\",\"note\":\"CI authorized disposal\",\"confirm\":true}" 200
COOKIE="$LIFE_COOKIE"; CSRF="$LIFE_CSRF"
api GET /stock/reconciliation "" 200
python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))==[]' "$BASE/result.json"
# Operational history is a hard gate even if a profile is disabled. No
# deletion of operational records is used to circumvent the restriction.
api POST /lifecycle/recoveries "{\"requestId\":\"$(req)\",\"equipmentId\":\"$ROS_EQUIPMENT\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_MAIN\",\"note\":\"CI blocked operational asset\",\"confirm\":true}" 409
grep -q 'operational profiles' "$BASE/result.json"
# Legacy asset intake creates one serial in quarantine, and only release
# credits stock. Repeated release cannot create a second stock record.
api POST /equipment "{\"highsiteId\":\"$SITE\",\"category\":\"cpe\",\"name\":\"CI legacy C6x\",\"model\":\"C6x\",\"serialNumber\":\"CI-LEGACY-025\"}" 201
LEGACY_ASSET="$(id)"
LEGACY_REC="$(req)"
api POST /lifecycle/recoveries "{\"requestId\":\"$LEGACY_REC\",\"equipmentId\":\"$LEGACY_ASSET\",\"itemId\":\"$ST_ITEM\",\"warehouseId\":\"$ST_MAIN\",\"note\":\"CI legacy recovered\",\"confirm\":true}" 201
api GET /equipment "" 200
python3 - "$BASE/result.json" "$LEGACY_ASSET" <<'PY'
import json,sys
assert all(x["id"]!=sys.argv[2] for x in json.load(open(sys.argv[1])))
PY
# Even an otherwise legitimate new backup enrollment must not reattach the
# old deployment identity. Database trigger guards concurrent enrollments.
api POST /backups/profiles "{\"equipmentId\":\"$LEGACY_ASSET\",\"name\":\"CI retired backup\",\"vendor\":\"mimosa\",\"confirm\":true}" 409
api POST "/lifecycle/recoveries/$LEGACY_REC/action" "{\"requestId\":\"$(req)\",\"action\":\"test_pass\",\"condition\":\"tested_used\",\"note\":\"CI legacy bench test\",\"confirm\":true}" 200
COOKIE="$APPROVER_COOKIE"; CSRF="$APPROVER_CSRF"
api POST "/lifecycle/recoveries/$LEGACY_REC/action" "{\"requestId\":\"$(req)\",\"action\":\"release\",\"note\":\"CI legacy independent approval\",\"confirm\":true}" 200
COOKIE="$LIFE_COOKIE"; CSRF="$LIFE_CSRF"
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc "SELECT count(*) FROM stock_serials WHERE serial='CI-LEGACY-025' AND warehouse_id='$ST_MAIN' AND equipment_id IS NULL AND condition='tested_used'" | grep -qx 1
api GET /stock/reconciliation "" 200
python3 -c 'import json,sys;assert json.load(open(sys.argv[1]))==[]' "$BASE/result.json"
echo "Equipment lifecycle smoke passed."
