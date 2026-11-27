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
api POST "/ipam/pools/$IPAM_POOL/unreserve" '{"address":"198.51.100.2","confirm":true}' 200
api GET /ipam/pools "" 200
echo "IPv4 IPAM smoke passed."
