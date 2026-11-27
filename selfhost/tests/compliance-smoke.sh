#!/usr/bin/env bash
# root/selfhost/tests/compliance-smoke.sh
# Synthetic stored snapshots exercise the evaluator without claiming physical
# device compatibility. The existing pinned HTTPS fixture covers transport.
api PUT "/compliance/devices/$ROS_ID/policy" "{\"expectedVersion\":\"7.19.3\",\"backupProfileId\":\"$VAULT\",\"backupMaxHours\":24,\"snapshotMaxHours\":24,\"requireBaseline\":true,\"pollIntervalMinutes\":0,\"confirm\":true}" 200
api GET "/compliance/devices/$ROS_ID" "" 200
grep -q '"status":"unknown"' "$BASE/result.json"
api POST "/compliance/devices/$ROS_ID/baseline" '{"reason":"Cannot approve failed inspection","confirm":true}' 409
python3 - "$BASE/compliance.sql" "$ROS_ID" <<'PY'
import json,sys
path,device=sys.argv[1:]
fields={
"resource":[{"version":"7.19.3 (stable)"}],
"identity":[{"name":"CI-Router"}],
"interfaces":[{"name":"ether1","type":"ether","mtu":"1500","disabled":"false","running":"true"}],
"vlans":[{"name":"clients","interface":"ether1","vlan-id":"100","disabled":"false"}],
"bridges":[],
"addresses":[{"address":"192.0.2.1/24","network":"192.0.2.0","interface":"ether1","disabled":"false","dynamic":"false"}],
"routes":[],
"pppoe":[],
"dhcp":[]
}
data={k:{"status":"ok","rows":v} for k,v in fields.items()}
with open(path,"w") as f:
 f.write("UPDATE routeros_devices SET enabled=true,last_poll_at=now(),last_status='ok',snapshot=$json$"+json.dumps(data)+"$json$::jsonb WHERE id='"+device+"';")
PY
docker exec -i "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 < "$BASE/compliance.sql" >/dev/null
api POST "/compliance/devices/$ROS_ID/baseline" '{"reason":"CI approved known configuration","confirm":true}' 201
api GET "/compliance/devices/$ROS_ID" "" 200
grep -q '"status":"compliant"' "$BASE/result.json"
# Runtime link status changes do not affect configuration baseline.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "UPDATE routeros_devices SET snapshot=jsonb_set(snapshot,'{interfaces,rows,0,running}','\"false\"') WHERE id='$ROS_ID'" >/dev/null
api GET "/compliance/devices/$ROS_ID" "" 200
grep -q '"status":"compliant"' "$BASE/result.json"
# VLAN drift and firmware mismatch are real failures.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "UPDATE routeros_devices SET snapshot=jsonb_set(jsonb_set(snapshot,'{vlans,rows,0,vlan-id}','\"200\"'),'{resource,rows,0,version}','\"7.20.1 (stable)\"') WHERE id='$ROS_ID'" >/dev/null
api GET "/compliance/devices/$ROS_ID" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
r=json.load(open(sys.argv[1])); checks={x["name"]:x["state"] for x in r["checks"]}
assert r["status"]=="noncompliant" and checks["vlans"]=="fail" and checks["routeros_version"]=="fail",r
PY
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "UPDATE routeros_devices SET last_poll_at=now()-interval '48 hours' WHERE id='$ROS_ID'" >/dev/null
api GET "/compliance/devices/$ROS_ID" "" 200
grep -q '"status":"unknown"' "$BASE/result.json"
api POST "/compliance/devices/$ROS_ID/baseline" '{"reason":"Stale evidence must be rejected","confirm":true}' 409
api GET "/compliance/devices/$ROS_ID/baselines" "" 200
python3 -c 'import json,sys;assert len(json.load(open(sys.argv[1])))==1' "$BASE/result.json"
api POST "/routeros/devices/$ROS_ID/enabled" '{"enabled":false,"confirm":true}' 200
echo "RouterOS compliance smoke passed."
