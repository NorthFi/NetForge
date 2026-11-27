#!/usr/bin/env bash
# root/selfhost/tests/ipam-smoke.sh
# Runs inside the existing disposable integration database, no extra logins.
api POST /ipam/pools '{"name":"CI IPv4","subnet":"198.51.100.0/29","gateway":"198.51.100.1","confirm":true}' 201
IPAM_POOL="$(id)"
api POST /ipam/pools '{"name":"CI overlap","subnet":"198.51.100.4/30","confirm":true}' 409
api POST /ipam/pools '{"name":"CI malformed","subnet":"198.51.100.1/29","confirm":true}' 400
api POST "/ipam/pools/$IPAM_POOL/reserve" '{"address":"198.51.100.2","reason":"Infrastructure","confirm":true}' 200
api POST "/ipam/pools/$IPAM_POOL/reserve" '{"address":"198.51.100.1","reason":"Gateway","confirm":true}' 409
api POST /services "{\"customerId\":\"$CUSTOMER\",\"packageId\":\"$PACKAGE\",\"username\":\"ci-ipam-reserved\",\"state\":\"pending\",\"staticIpv4\":\"198.51.100.2\"}" 409
api POST /services "{\"customerId\":\"$CUSTOMER\",\"packageId\":\"$PACKAGE\",\"username\":\"ci-ipam-pending\",\"state\":\"pending\"}" 201
IPAM_SERVICE="$(id)"
api POST "/ipam/pools/$IPAM_POOL/allocate" "{\"serviceId\":\"$IPAM_SERVICE\",\"address\":\"198.51.100.2\",\"confirm\":true}" 409
api POST "/ipam/pools/$IPAM_POOL/allocate" "{\"serviceId\":\"$IPAM_SERVICE\",\"confirm\":true}" 200
grep -q '"address":"198.51.100.3"' "$BASE/result.json"
api POST "/ipam/pools/$IPAM_POOL/release" "{\"serviceId\":\"$IPAM_SERVICE\",\"confirm\":true}" 200
api POST "/ipam/pools/$IPAM_POOL/allocate" "{\"serviceId\":\"$IPAM_SERVICE\",\"confirm\":true}" 200
grep -q '"address":"198.51.100.3"' "$BASE/result.json"
api POST "/services/$IPAM_SERVICE/credentials" '{"password":"ci-ipam-password-2026"}' 200
api POST "/ipam/pools/$IPAM_POOL/release" "{\"serviceId\":\"$IPAM_SERVICE\",\"confirm\":true}" 409
api GET "/ipam/pools/$IPAM_POOL/addresses" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows={x["address"]:x for x in json.load(open(sys.argv[1]))}
assert len(rows)==6,rows
assert rows["198.51.100.1"]["state"]=="gateway",rows
assert rows["198.51.100.2"]["state"]=="reserved",rows
assert rows["198.51.100.3"]["state"]=="assigned",rows
assert rows["198.51.100.3"]["radius_configured"] is True,rows
PY
# Two independent external records must block otherwise unassigned hosts.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
 INSERT INTO radacct(acctsessionid,acctuniqueid,username,nasipaddress,acctstarttime,acctupdatetime,framedipaddress)
 VALUES('CI-IPAM-HOLD','CI-IPAM-HOLD','ci-external-ipam','192.0.2.1',now(),now(),'198.51.100.4');
 INSERT INTO radreply(username,attribute,op,value)
 VALUES('ci-external-static','Framed-IP-Address',':=','198.51.100.5');
" >/dev/null
api POST /services "{\"customerId\":\"$CUSTOMER\",\"packageId\":\"$PACKAGE\",\"username\":\"ci-ipam-race-a\",\"state\":\"pending\"}" 201
IPAM_A="$(id)"
api POST /services "{\"customerId\":\"$CUSTOMER\",\"packageId\":\"$PACKAGE\",\"username\":\"ci-ipam-race-b\",\"state\":\"pending\"}" 201
IPAM_B="$(id)"
api POST "/ipam/pools/$IPAM_POOL/allocate" "{\"serviceId\":\"$IPAM_A\",\"address\":\"198.51.100.4\",\"confirm\":true}" 409
api POST "/ipam/pools/$IPAM_POOL/allocate" "{\"serviceId\":\"$IPAM_A\",\"address\":\"198.51.100.5\",\"confirm\":true}" 409
# With gateway, reservation, assignment and two holds, exactly one host is left.
# Two concurrent first-free requests must produce one success and one conflict.
ipam_race(){
 local service="$1" output="$2"
 curl -sS -o "$BASE/$output.json" -w '%{http_code}\n' -X POST \
  -H "Cookie: $COOKIE" -H "X-CSRF-Token: $CSRF" -H 'Content-Type: application/json' \
  --data "{\"serviceId\":\"$service\",\"confirm\":true}" \
  "$BASEURL/api/ipam/pools/$IPAM_POOL/allocate" > "$BASE/$output.code"
}
ipam_race "$IPAM_A" ipam-a & IPAM_PID_A=$!
ipam_race "$IPAM_B" ipam-b & IPAM_PID_B=$!
wait "$IPAM_PID_A"; wait "$IPAM_PID_B"
test "$(sort "$BASE/ipam-a.code" "$BASE/ipam-b.code" | paste -sd, -)" = '200,409'
api GET "/ipam/pools/$IPAM_POOL/addresses" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows={x["address"]:x for x in json.load(open(sys.argv[1]))}
assert rows["198.51.100.4"]["state"]=="accounting_hold",rows
assert rows["198.51.100.5"]["state"]=="radius_hold",rows
assert rows["198.51.100.6"]["state"]=="assigned",rows
assert all(x["state"]!="free" for x in rows.values()),rows
PY
api POST "/ipam/pools/$IPAM_POOL/unreserve" '{"address":"198.51.100.2","confirm":true}' 200
api GET /ipam/pools "" 200
echo "IPv4 IPAM smoke passed."
