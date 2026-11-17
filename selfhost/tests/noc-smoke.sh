#!/usr/bin/env bash
# root/selfhost/tests/noc-smoke.sh
# Integration coverage for NOC incident correlation, triage and CRM links.
# Sourced after RF telemetry tests from the authenticated API smoke runner.
set -Eeuo pipefail

api GET /noc/summary "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))['summary']
assert int(r['monitoring'])>=1,r
assert int(r['potential_customer_exposure'])>=1,r
PY

api GET '/noc/incidents?state=active' "" 200
ORIGINAL_NOC="$(python3 - "$BASE/result.json" "$SITE" <<'PY'
import json,sys
rows=[r for r in json.load(open(sys.argv[1])) if r['highsite_id']==sys.argv[2]]
assert len(rows)==1,rows
row=rows[0]
assert row['origin']=='monitor' and row['status']=='monitoring',row
assert row['recovered_at'] is not None and row['resolved_at'] is None,row
assert int(row['potential_customers'])>=1,row
assert int(row['sectors_snapshot'])>=1,row
print(row['id'])
PY
)"
api GET "/noc/incidents/$ORIGINAL_NOC/customers?limit=100&offset=0" "" 200
python3 - "$BASE/result.json" "$CUSTOMER" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
assert any(c['customer_id']==sys.argv[2] and
 c['account_snapshot']=='CI-001' for c in r['customers']),r
PY
api GET "/noc/incidents/$ORIGINAL_NOC/history" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
actions=[x['action'] for x in json.load(open(sys.argv[1]))]
assert 'detected' in actions and 'upstream_recovered' in actions,actions
PY
api POST "/noc/incidents/$ORIGINAL_NOC/acknowledge" '{}' 409
api POST "/noc/incidents/$ORIGINAL_NOC/notes" '{"note":"Verified access equipment power and upstream routing."}' 200
api POST "/noc/incidents/$ORIGINAL_NOC/assign" '{"username":"ciadmin"}' 200
api POST "/noc/incidents/$ORIGINAL_NOC/assign" '{"username":"nonexistent-staff"}' 409

# Only existing highsite tickets of the same site may be linked. A customer
# support ticket with highsite_id set is still not a highsite-level ticket.
api POST "/noc/incidents/$ORIGINAL_NOC/ticket" "{\"ticketId\":\"$TICKET\",\"subject\":\"\",\"description\":\"\",\"confirm\":true}" 409
api POST "/noc/incidents/$ORIGINAL_NOC/ticket" '{"ticketId":null,"subject":"NOC: upstream recovered","description":"Staff investigate site traffic and verify client access.","confirm":true}' 200
NOC_TICKET="$(python3 - "$BASE/result.json" <<'PY'
import json,sys
print(json.load(open(sys.argv[1]))['ticketId'])
PY
)"
api POST "/noc/incidents/$ORIGINAL_NOC/ticket" '{"ticketId":null,"subject":"","description":"","confirm":true}' 409
api GET /tickets "" 200
python3 - "$BASE/result.json" "$NOC_TICKET" "$SITE" <<'PY'
import json,sys
t=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert t['type']=='highsite' and t['highsite_id']==sys.argv[3],t
assert t['customer_id'] is None,t
PY
api POST "/noc/incidents/$ORIGINAL_NOC/resolve" '{"resolution":"Restored verified connectivity to upstream and subscriber sessions.","confirm":true}' 200
api POST "/noc/incidents/$ORIGINAL_NOC/resolve" '{"resolution":"Duplicate resolution attempt","confirm":true}' 409
api GET "/noc/incidents/$ORIGINAL_NOC" "" 200
python3 - "$BASE/result.json" "$NOC_TICKET" <<'PY'
import json,sys
row=json.load(open(sys.argv[1]))
assert row['status']=='resolved' and row['resolved_at'] is not None,row
assert row['ticket_id']==sys.argv[2],row
PY

# After resolving an incident, the next independent DOWN is a new event.
api PUT "/monitor/targets/$MONITOR_ID" '{"enabled":true,"intervalSeconds":300,"failureThreshold":2,"confirm":true}' 200
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
api GET '/noc/incidents?state=active' "" 200
SECOND_NOC="$(python3 - "$BASE/result.json" "$SITE" "$ORIGINAL_NOC" <<'PY'
import json,sys
rows=[x for x in json.load(open(sys.argv[1])) if x['highsite_id']==sys.argv[2]]
assert len(rows)==1,rows
assert rows[0]['id']!=sys.argv[3] and rows[0]['status']=='open',rows
print(rows[0]['id'])
PY
)"
api POST "/noc/incidents/$SECOND_NOC/acknowledge" '{}' 200
api POST "/noc/incidents/$SECOND_NOC/acknowledge" '{}' 409
api POST "/noc/incidents/$SECOND_NOC/resolve" '{"resolution":"Attempt to resolve while critical uplink is still down","confirm":true}' 409
api POST /noc/incidents "{\"highsiteId\":\"$SITE\",\"summary\":\"Duplicate tower fault\",\"severity\":\"high\",\"confirm\":true}" 409
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
api GET '/noc/incidents?state=active' "" 200
python3 - "$BASE/result.json" "$SITE" <<'PY'
import json,sys
rows=[x for x in json.load(open(sys.argv[1])) if x['highsite_id']==sys.argv[2]]
assert len(rows)==1,rows
PY

python3 -m http.server 19092 --bind 127.0.0.1 > "$BASE/noc-http.log" 2>&1 &
MONITOR_PID="$!"
sleep 0.35
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
grep -q '"transition":"recovered"' "$BASE/result.json"
api GET "/noc/incidents/$SECOND_NOC" "" 200
grep -q '"status":"monitoring"' "$BASE/result.json"
api POST "/noc/incidents/$SECOND_NOC/resolve" '{"resolution":"Verified transport restored and service reachability tested.","confirm":true}' 200
kill "$MONITOR_PID" 2>/dev/null || true
MONITOR_PID=""
api PUT "/monitor/targets/$MONITOR_ID" '{"enabled":false,"intervalSeconds":300,"failureThreshold":2,"confirm":false}' 200

# Manual incidents are internal and do not need a monitor event or auto
# recovery; they still snapshot the site and enforce highsite uniqueness.
api POST /highsites "{\"number\":\"HS-CI-NOC-MANUAL\",\"name\":\"Manual-only NOC highsite\",\"address\":\"Test\",\"branchId\":\"$BRANCH\",\"latitude\":null,\"longitude\":null}" 201
NOC_MANUAL_SITE="$(id)"
api POST /noc/incidents "{\"highsiteId\":\"$NOC_MANUAL_SITE\",\"summary\":\"Power interruption reported by technician\",\"severity\":\"critical\",\"confirm\":true}" 201
MANUAL_NOC="$(id)"
api GET "/noc/incidents/$MANUAL_NOC/customers" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
assert json.load(open(sys.argv[1]))['customers']==[]
PY
api POST "/noc/incidents/$MANUAL_NOC/acknowledge" '{}' 200
api POST "/noc/incidents/$MANUAL_NOC/resolve" '{"resolution":"Technician confirmed power returned and equipment accessible.","confirm":true}' 200

# Staff-only metadata and customer snapshot must never be readable with
# the separate subscriber portal cookie or without a staff session.
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
 -H "Cookie: $NEW_PORTAL_COOKIE" "$BASEURL/api/noc/incidents")"
test "$CODE" = 401
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
 "$BASEURL/api/noc/incidents/$ORIGINAL_NOC/customers")"
test "$CODE" = 401
api GET '/noc/incidents?state=resolved' "" 200
python3 - "$BASE/result.json" "$ORIGINAL_NOC" "$SECOND_NOC" "$MANUAL_NOC" <<'PY'
import json,sys
ids={x['id'] for x in json.load(open(sys.argv[1]))}
assert {sys.argv[2],sys.argv[3],sys.argv[4]}.issubset(ids),ids
PY
api GET "/noc/incidents/$ORIGINAL_NOC/history" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
actions=[x['action'] for x in json.load(open(sys.argv[1]))]
for required in ['detected','upstream_recovered','note','assigned','ticket_linked','resolved']:
    assert required in actions,actions
PY
echo "NOC incident lifecycle, deduplication, site exposure and CRM ticket integration passed."
