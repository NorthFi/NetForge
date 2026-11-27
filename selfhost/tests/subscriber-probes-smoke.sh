#!/usr/bin/env bash
# root/selfhost/tests/subscriber-probes-smoke.sh
# Synthetic agent credentials and endpoints, isolated PostgreSQL CI fixture.
set -Eeuo pipefail
api GET /assurance/subscriber-probes "" 200
test "$(cat "$BASE/result.json")" = '[]'
ENROLL="{\"serviceId\":\"$SERVICE\",\"name\":\"CI subscriber LAN\",\"dnsHost\":\"probe.example.test\",\"httpsUrl\":\"https://probe.example.test/health\",\"expectedStatus\":204,\"intervalSeconds\":60,\"confirm\":true}"
api POST /assurance/subscriber-probes "$ENROLL" 201
PROBE_ID="$(id)"
PROBE_TOKEN="$(python3 - "$BASE/result.json" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
assert len(r['token'])==64
print(r['token'])
PY
)"
api POST /assurance/subscriber-probes "$ENROLL" 409
api GET /assurance/subscriber-probes "" 200
python3 - "$BASE/result.json" "$PROBE_ID" <<'PY'
import json,sys
r=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert r['subscriber_evidence']=='unknown',r
assert 'token' not in r and 'token_hash' not in r,r
PY
# Generate bounded report payloads without depending on a public endpoint.
probe_payload() {
 python3 - "$1" "$2" "$3" <<'PY'
import json,sys,uuid,datetime
now=datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=int(sys.argv[3]))
dns,https=sys.argv[1:3]
print(json.dumps(dict(measurementId=str(uuid.uuid4()),measuredAt=now.isoformat(),
 dnsHost='probe.example.test',httpsUrl='https://probe.example.test/health',
 dnsResult=dns,dnsMs=12,httpsResult=https,httpsMs=25,
 httpsStatus=204 if https=='ok' else None)))
PY
}
probe_post() {
 local token="$1" payload="$2" expected="$3" actual
 actual="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Authorization: Bearer $token" -H 'Content-Type: application/json' \
  --data "$payload" "$BASEURL/api/probe-report/$PROBE_ID")"
 if [ "$actual" != "$expected" ]; then
  echo "Probe report expected $expected got $actual" >&2
  cat "$BASE/result.json" >&2;exit 1
 fi
}
REPORT="$(probe_payload ok ok 0)"
probe_post invalid "$REPORT" 401
probe_post "$(printf '%064d' 0)" "$REPORT" 401
probe_post "$PROBE_TOKEN" "$(probe_payload ok ok -601)" 400
probe_post "$PROBE_TOKEN" "$(probe_payload ok ok 120)" 400
probe_post "$PROBE_TOKEN" "${REPORT/probe.example.test/other.example.test}" 400
probe_post "$PROBE_TOKEN" "$REPORT" 200
grep -q '"replayed":false' "$BASE/result.json"
probe_post "$PROBE_TOKEN" "$REPORT" 200
grep -q '"replayed":true' "$BASE/result.json"
probe_post "$PROBE_TOKEN" "$(probe_payload ok failed 0)" 429
api GET /assurance/subscriber-probes "" 200
python3 - "$BASE/result.json" "$PROBE_ID" <<'PY'
import json,sys
r=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert r['subscriber_evidence']=='checks_passed',r
assert r['dns_result']=='ok' and r['https_status']==204,r
assert r['cpe_evidence']=='unknown',r
assert r['radius_evidence'] in ('unknown','recent_accounting'),r
PY
api GET "/assurance/subscriber-probes/$PROBE_ID/history?hours=24" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
assert len(json.load(open(sys.argv[1])))==1
PY
# Age fixture timestamps to exercise freshness without sleeping 3 minutes.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
 UPDATE subscriber_probe_reports SET measured_at=now()-interval '4 minutes',
 received_at=now()-interval '4 minutes' WHERE agent_id='$PROBE_ID';
 UPDATE subscriber_probe_agents SET last_measured_at=now()-interval '4 minutes',
 last_received_at=now()-interval '4 minutes' WHERE id='$PROBE_ID';
" >/dev/null
api GET /assurance/subscriber-probes "" 200
grep -q '"subscriber_evidence":"unknown"' "$BASE/result.json"
probe_post "$PROBE_TOKEN" "$(probe_payload failed timeout -300)" 409
probe_post "$PROBE_TOKEN" "$(probe_payload failed timeout 0)" 200
api GET /assurance/subscriber-probes "" 200
grep -q '"subscriber_evidence":"check_failed"' "$BASE/result.json"
api POST "/assurance/subscriber-probes/$PROBE_ID/rotate" '{"confirm":true}' 200
NEW_PROBE_TOKEN="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["token"])' "$BASE/result.json")"
test "$NEW_PROBE_TOKEN" != "$PROBE_TOKEN"
probe_post "$PROBE_TOKEN" "$(probe_payload ok ok 0)" 401
api GET /assurance/subscriber-probes "" 200
grep -q '"subscriber_evidence":"unknown"' "$BASE/result.json"
probe_post "$NEW_PROBE_TOKEN" "$(probe_payload ok ok 0)" 200
api GET "/assurance/subscriber-probes/$PROBE_ID/history?hours=24" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)==3,rows
assert [x['generation'] for x in rows]==[1,1,2],rows
PY
api POST "/assurance/subscriber-probes/$PROBE_ID/revoke" '{"confirm":true}' 200
probe_post "$NEW_PROBE_TOKEN" "$(probe_payload ok ok 0)" 401
api GET /assurance/subscriber-probes "" 200
grep -q '"subscriber_evidence":"disabled"' "$BASE/result.json"
# Probe observations cannot create an interruption or trigger billing.
api GET "/assurance/interruptions?serviceId=$SERVICE" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
assert len(json.load(open(sys.argv[1])))==1
PY
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
 -H "Cookie: $NEW_PORTAL_COOKIE" "$BASEURL/api/assurance/subscriber-probes")"
test "$CODE" = 401
echo "Subscriber probe ingestion, expiry, replay, credential isolation and evidence tests passed."
