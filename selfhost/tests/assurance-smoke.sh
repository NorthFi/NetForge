#!/usr/bin/env bash
# root/selfhost/tests/assurance-smoke.sh
# End-to-end .NET/PostgreSQL RADIUS evidence and conservative assurance reports.
# Run after the NOC phase created, recovered and resolved its test incidents.
set -Eeuo pipefail

api GET /assurance/policy "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
row=json.load(open(sys.argv[1]))['settings']
assert float(row['target_percent'])==99.5,row
assert row['accounting_freshness_minutes']==10,row
PY
api GET "/assurance/incidents/$ORIGINAL_NOC/evidence" "" 200
python3 - "$BASE/result.json" "$SERVICE" <<'PY'
import json,sys
result=json.load(open(sys.argv[1]))
services=result['records']
assert any(x['service_id']==sys.argv[2] for x in services),result
for row in services:
 assert row['evidence_state'] in ('recent_accounting','stale_accounting',
  'recent_stop_observed','no_accounting_data'),row
 assert row['username_snapshot'],row
PY
api GET "/assurance/incidents/$ORIGINAL_NOC/evidence/$SERVICE/history" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)==1,rows
PY

api POST "/assurance/incidents/$ORIGINAL_NOC/refresh" '{"confirm":false}' 400
api POST "/assurance/incidents/$ORIGINAL_NOC/refresh" '{"confirm":true}' 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
assert json.load(open(sys.argv[1]))['captured']>=1
PY
api GET "/assurance/incidents/$ORIGINAL_NOC/evidence/$SERVICE/history" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
assert len(json.load(open(sys.argv[1])))==2
PY
api GET "/noc/incidents/$ORIGINAL_NOC/history" "" 200
grep -q '"action":"radius_evidence"' "$BASE/result.json"

# Synthetic ongoing session with a fresh RADIUS interim update.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
  INSERT INTO radacct(acctsessionid,acctuniqueid,username,nasipaddress,
                      acctstarttime,acctupdatetime,acctinputoctets,acctoutputoctets)
  VALUES ('nf-assurance-ci','nf-assurance-ci',
          'ci-pppoe@example.test','192.0.2.1',
          now()-interval '15 minutes',now(),0,0)
  ON CONFLICT(acctuniqueid) DO NOTHING;
" >/dev/null
api POST "/assurance/incidents/$ORIGINAL_NOC/refresh" '{"confirm":true}' 200
api GET "/assurance/incidents/$ORIGINAL_NOC/evidence" "" 200
python3 - "$BASE/result.json" "$SERVICE" <<'PY'
import json,sys
row=next(x for x in json.load(open(sys.argv[1]))['records'] if x['service_id']==sys.argv[2])
assert row['evidence_state']=='recent_accounting',row
assert row['recent_open_sessions']>=1 and row['open_sessions']>=1,row
PY
# Recording session termination does not create a downtime interval.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
  UPDATE radacct SET acctstoptime=now(),acctupdatetime=now(),
    acctterminatecause='Lost-Carrier'
  WHERE acctuniqueid='nf-assurance-ci';
" >/dev/null
api POST "/assurance/incidents/$ORIGINAL_NOC/refresh" '{"confirm":true}' 200
api GET "/assurance/incidents/$ORIGINAL_NOC/evidence" "" 200
python3 - "$BASE/result.json" "$SERVICE" <<'PY'
import json,sys
row=next(x for x in json.load(open(sys.argv[1]))['records'] if x['service_id']==sys.argv[2])
assert row['last_session_stop_at'] is not None,row
assert row['stop_reason']=='Lost-Carrier',row
PY
api GET "/assurance/interruptions?serviceId=$SERVICE" "" 200
test "$(cat "$BASE/result.json")" = '[]'

# Operator-verified interval is backdated within the test service lifecycle.
# Only CI metadata is changed; no production data and no billing balance.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
  UPDATE services SET created_at=now()-interval '4 days'
  WHERE id='$SERVICE';
" >/dev/null

TEST_START="$(date -u -d '45 minutes ago' +'%Y-%m-%dT%H:%M:%SZ')"
TEST_END="$(date -u -d '15 minutes ago' +'%Y-%m-%dT%H:%M:%SZ')"
TEST_MONTH="$(date -u -d '45 minutes ago' +'%Y-%m')"

api GET "/assurance/reports/monthly?period=$TEST_MONTH&customerId=$CUSTOMER" "" 200
python3 - "$BASE/result.json" "$SERVICE" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
s=next(x for x in r['services'] if x['service_id']==sys.argv[2])
assert s['finding']=='insufficient_evidence_for_compliance',s
assert float(s['staff_attested_downtime_minutes'])==0,s
assert int(s['recorded_stops'])>=1,s
assert r['classification'].startswith('INTERNAL EVIDENCE REPORT'),r
PY
api PUT /assurance/policy '{"targetPercent":100,"accountingFreshnessMinutes":10,"confirm":true}' 400
api PUT /assurance/policy '{"targetPercent":99.999,"accountingFreshnessMinutes":10,"confirm":true}' 200

PAYLOAD="{\"serviceId\":\"$SERVICE\",\"incidentId\":\"$ORIGINAL_NOC\",\"ticketId\":null,\"startedAt\":\"$TEST_START\",\"endedAt\":\"$TEST_END\",\"evidence\":\"Staff verified outage using a separate authenticated subscriber connectivity test and technician NOC review.\",\"confirm\":true}"
api POST /assurance/interruptions '{"serviceId":"00000000-0000-0000-0000-000000000000","incidentId":null,"ticketId":null,"startedAt":"2026-01-01T01:00:00Z","endedAt":"2026-01-01T01:10:00Z","evidence":"This must never create a row","confirm":true}' 400
api POST /assurance/interruptions "$PAYLOAD" 201
INTERRUPTION="$(id)"
api POST /assurance/interruptions "$PAYLOAD" 409
api GET "/assurance/interruptions?serviceId=$SERVICE" "" 200
python3 - "$BASE/result.json" "$INTERRUPTION" "$ORIGINAL_NOC" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
assert len(r)==1,r
assert r[0]['id']==sys.argv[2] and r[0]['incident_id']==sys.argv[3],r
assert float(r[0]['documented_minutes'])>=29,r
assert r[0]['source']=='staff_attested',r
PY
api GET "/assurance/reports/monthly?period=$TEST_MONTH&customerId=$CUSTOMER" "" 200
python3 - "$BASE/result.json" "$SERVICE" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
s=next(x for x in r['services'] if x['service_id']==sys.argv[2])
assert s['finding']=='documented_threshold_exceeded',s
assert float(s['staff_attested_downtime_minutes'])>0,s
assert float(s['internal_threshold_budget_minutes'])<float(s['staff_attested_downtime_minutes']),s
assert float(s['maximum_possible_availability_percent'])<100,s
assert int(s['recorded_stops'])>=1,s
PY

# The staff API cannot be reused with the subscriber portal cookie.
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Cookie: $NEW_PORTAL_COOKIE" "$BASEURL/api/assurance/reports/monthly")"
test "$CODE" = 401
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  "$BASEURL/api/assurance/incidents/$ORIGINAL_NOC/evidence")"
test "$CODE" = 401
echo "Subscriber assurance evidence, monthly screening and staff-attested intervals passed."
