#!/usr/bin/env bash
# root/selfhost/tests/sla-probes-smoke.sh
# CI: immutable SLA terms, dual-control credits, balanced journals, CPE reachability.
# All targets/addresses are isolated localhost test fixtures.
set -Eeuo pipefail

TODAY="$(date -u +%F)"
TOMORROW="$(date -u -d 'tomorrow' +%F)"
api GET "/assurance/sla-versions?serviceId=$SERVICE" "" 200
test "$(cat "$BASE/result.json")" = '[]'
api POST /assurance/sla-versions "{\"serviceId\":\"$SERVICE\",\"effectiveFrom\":\"$TODAY\",\"targetPercent\":99.9,\"creditCapPercent\":10,\"agreementReference\":\"CI-Service-SLA-v1\",\"exclusions\":\"Planned maintenance is investigated separately\",\"confirm\":false}" 400
api POST /assurance/sla-versions "{\"serviceId\":\"$SERVICE\",\"effectiveFrom\":\"$TODAY\",\"targetPercent\":99.9,\"creditCapPercent\":10,\"agreementReference\":\"CI-Service-SLA-v1\",\"exclusions\":\"Planned maintenance separately classified\",\"confirm\":true}" 201
SLA_V1="$(id)"
api POST /assurance/sla-versions "{\"serviceId\":\"$SERVICE\",\"effectiveFrom\":\"$TOMORROW\",\"targetPercent\":99.7,\"creditCapPercent\":5,\"agreementReference\":\"CI-Service-SLA-v2\",\"exclusions\":\"Test second version\",\"confirm\":true}" 201
SLA_V2="$(id)"
api POST /assurance/sla-versions "{\"serviceId\":\"$SERVICE\",\"effectiveFrom\":\"$TODAY\",\"targetPercent\":99.9,\"creditCapPercent\":10,\"agreementReference\":\"Duplicate retroactive entry\",\"exclusions\":\"\",\"confirm\":true}" 409
api GET "/assurance/sla-versions?serviceId=$SERVICE" "" 200
python3 - "$BASE/result.json" "$SLA_V1" "$SLA_V2" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)==2,rows
assert {x['id'] for x in rows}==set(sys.argv[2:]),rows
assert sorted(x['version'] for x in rows)==[1,2],rows
PY

# The immutable term schedule cannot be changed even by a DB operator.
if docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 \
 -c "UPDATE assurance_sla_versions SET target_percent=90 WHERE id='$SLA_V1';" \
 > "$BASE/immutable.stdout" 2> "$BASE/immutable.stderr"; then
  echo "SLA terms unexpectedly mutable" >&2;exit 1
fi

api POST /invoices "{\"customerId\":\"$CUSTOMER\",\"number\":\"CI-SLA-CREDIT-INVOICE\",\"total\":500,\"dueDate\":\"2027-06-01\"}" 201
SLA_INVOICE="$(id)"
api POST "/invoices/$SLA_INVOICE/issue" '{}' 200

PROPOSAL_BASE="{\"serviceId\":\"$SERVICE\",\"interruptionId\":\"$INTERRUPTION\",\"slaVersionId\":\"$SLA_V1\",\"invoiceId\":\"$SLA_INVOICE\",\"amount\":25,\"reason\":\"Verified half-hour service outage credit review\",\"confirm\":true}"
api POST /assurance/credit-requests "{\"serviceId\":\"$SERVICE\",\"interruptionId\":\"$INTERRUPTION\",\"slaVersionId\":\"$SLA_V1\",\"invoiceId\":\"$SLA_INVOICE\",\"amount\":60,\"reason\":\"Intentional credit cap violation\",\"confirm\":true}" 409
api POST /assurance/credit-requests "$PROPOSAL_BASE" 201
SLA_CREDIT_REQUEST="$(id)"
api POST /assurance/credit-requests "$PROPOSAL_BASE" 409
api POST "/assurance/credit-requests/$SLA_CREDIT_REQUEST/review" '{"approve":true,"reason":"Investigated verified supporting incident and invoice.","confirm":true}' 409
api POST "/assurance/credit-requests/$SLA_CREDIT_REQUEST/post" '{"confirm":true}' 409

# Second administrator must approve the credit; the original proposer
# cannot review the request. No shared credentials or impersonation.
api POST /staff '{"username":"ci-sla-approver","password":"ci_approver_unique_password_2026","role":"admin"}' 201
APPROVER_STATUS="$(curl -sS -D "$BASE/approver.headers" -o "$BASE/result.json" -w '%{http_code}' \
 -H 'Content-Type: application/json' -X POST "$BASEURL/api/login" \
 --data '{"username":"ci-sla-approver","password":"ci_approver_unique_password_2026"}')"
test "$APPROVER_STATUS" = 200
APPROVER_COOKIE="$(grep -i '^set-cookie: NetForge.Auth=' "$BASE/approver.headers" |
  head -1 | sed -E 's/^[^:]*: ([^;]*).*/\1/' | tr -d '\r')"
APPROVER_CSRF="$(python3 - "$BASE/result.json" <<'PY'
import json,sys
print(json.load(open(sys.argv[1]))['csrf'])
PY
)"
test -n "$APPROVER_COOKIE" && test -n "$APPROVER_CSRF"
ORIGINAL_COOKIE="$COOKIE";ORIGINAL_CSRF="$CSRF"
COOKIE="$APPROVER_COOKIE";CSRF="$APPROVER_CSRF"
api POST "/assurance/credit-requests/$SLA_CREDIT_REQUEST/review" '{"approve":true,"reason":"Reviewed separately with test service incident evidence.","confirm":true}' 200
grep -q '"status":"approved"' "$BASE/result.json"
COOKIE="$ORIGINAL_COOKIE";CSRF="$ORIGINAL_CSRF"

# Approval alone did NOT create a credit note or change the invoice balance.
api GET /billing/credits "" 200
python3 - "$BASE/result.json" "$SLA_INVOICE" <<'PY'
import json,sys
assert not any(x['invoice_id']==sys.argv[2] for x in json.load(open(sys.argv[1])))
PY
api POST "/assurance/credit-requests/$SLA_CREDIT_REQUEST/post" '{"confirm":false}' 400
api POST "/assurance/credit-requests/$SLA_CREDIT_REQUEST/post" '{"confirm":true}' 200
CREDIT_NOTE_ID="$(python3 - "$BASE/result.json" <<'PY'
import json,sys
data=json.load(open(sys.argv[1]))
assert data['ledgerPosted'] is True and data['replayed'] is False,data
assert float(data['remaining'])==475,data
print(data['creditNoteId'])
PY
)"
api POST "/assurance/credit-requests/$SLA_CREDIT_REQUEST/post" '{"confirm":true}' 200
grep -q '"replayed":true' "$BASE/result.json"
api GET /billing/credits "" 200
python3 - "$BASE/result.json" "$CREDIT_NOTE_ID" "$SLA_INVOICE" <<'PY'
import json,sys
rows=[x for x in json.load(open(sys.argv[1])) if x['invoice_id']==sys.argv[3]]
assert len(rows)==1,rows
assert rows[0]['id']==sys.argv[2] and float(rows[0]['amount'])==25,rows
PY
api GET /billing/ledger "" 200
python3 - "$BASE/result.json" "$CREDIT_NOTE_ID" <<'PY'
import json,sys
rows=[x for x in json.load(open(sys.argv[1]))
 if x['source_type']=='credit_note' and x['source_id']==sys.argv[2]]
assert len(rows)==2,rows
assert {(x['account_code'],float(x['debit']),float(x['credit'])) for x in rows}=={
 ('REVENUE_RETURNS',25.0,0.0),('AR',0.0,25.0)},rows
PY
api GET "/assurance/credit-requests?status=posted" "" 200
python3 - "$BASE/result.json" "$SLA_CREDIT_REQUEST" <<'PY'
import json,sys
row=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert row['reviewed_by']=='ci-sla-approver' and row['proposed_by']=='ciadmin',row
assert row['credit_note_id'] is not None and row['status']=='posted',row
PY

# Independently sampled management TCP reachability is NOT converted to
# staff-attested interruption minutes.
api POST /equipment "{\"customerId\":\"$CUSTOMER\",\"highsiteId\":null,\"category\":\"cpe\",\"name\":\"CI CPE management endpoint\",\"model\":\"CI localhost fixture\",\"managementIp\":\"127.0.0.1\",\"serialNumber\":\"CI-CPE-0001\",\"ownership\":\"CI test\"}" 201
CPE_EQUIPMENT="$(id)"
api POST /equipment "{\"customerId\":null,\"highsiteId\":\"$SITE\",\"category\":\"cpe\",\"name\":\"Other site equipment\",\"model\":\"Fixture\",\"managementIp\":\"127.0.0.1\",\"serialNumber\":\"CI-OTHER-EQUIP\",\"ownership\":\"CI test\"}" 201
OTHER_EQUIPMENT="$(id)"
api POST /assurance/cpe-probes "{\"serviceId\":\"$SERVICE\",\"equipmentId\":\"$OTHER_EQUIPMENT\",\"tcpPort\":8291,\"intervalSeconds\":300,\"enabled\":false,\"confirm\":false}" 409
api POST /assurance/cpe-probes "{\"serviceId\":\"$SERVICE\",\"equipmentId\":\"$CPE_EQUIPMENT\",\"tcpPort\":8291,\"intervalSeconds\":300,\"enabled\":false,\"confirm\":false}" 201
CPE_PROBE_ID="$(id)"
api POST "/assurance/cpe-probes/$CPE_PROBE_ID/check" '{}' 200
grep -q '"ok":false' "$BASE/result.json"
api PUT "/assurance/cpe-probes/$CPE_PROBE_ID" '{"tcpPort":8291,"intervalSeconds":300,"enabled":true,"confirm":true}' 200
python3 -m http.server 8291 --bind 127.0.0.1 > "$BASE/cpe-http.log" 2>&1 &
MONITOR_PID="$!"
sleep 0.4
api POST "/assurance/cpe-probes/$CPE_PROBE_ID/check" '{}' 200
grep -q '"reachable":true' "$BASE/result.json"
kill "$MONITOR_PID" 2>/dev/null || true
wait "$MONITOR_PID" 2>/dev/null || true
MONITOR_PID=""
for attempt in 1 2 3; do
 api POST "/assurance/cpe-probes/$CPE_PROBE_ID/check" '{}' 200
done
grep -q '"state":"down"' "$BASE/result.json"
api GET "/assurance/cpe-probes/$CPE_PROBE_ID/history?hours=24" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)>=4,rows
assert rows[0]['reachable'] is True,rows
assert rows[-1]['reachable'] is False and rows[-1]['state']=='down',rows
PY
api GET "/assurance/interruptions?serviceId=$SERVICE" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
assert len(json.load(open(sys.argv[1])))==1
PY
api PUT "/assurance/cpe-probes/$CPE_PROBE_ID" '{"tcpPort":8291,"intervalSeconds":300,"enabled":false,"confirm":false}' 200

# Separate customer-portal sessions cannot view SLA agreements, credits or CPE
# management addresses through staff-only operations endpoints.
for endpoint in '/assurance/sla-versions' '/assurance/credit-requests' '/assurance/cpe-probes'; do
 CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
   -H "Cookie: $NEW_PORTAL_COOKIE" "$BASEURL/api$endpoint")"
 test "$CODE" = 401
done
echo "SLA versions, independent credit approval/posting, ledger idempotence and CPE probes passed."
