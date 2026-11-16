#!/usr/bin/env bash
# root/selfhost/tests/smoke.sh
set -Eeuo pipefail
BASE="$(mktemp -d)"
PID=""
MOCK_PID=""
trap 'if [ -n "$MOCK_PID" ]; then kill "$MOCK_PID" 2>/dev/null || true; fi; if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; fi; cat "$BASE/server.log" 2>/dev/null | tail -35 || true; rm -rf "$BASE"' EXIT
export ASPNETCORE_URLS="http://127.0.0.1:18990"
export Database__ConnectionString="Host=127.0.0.1;Port=55432;Database=netforge_ci;Username=netforge_ci;Password=netforge_test_very_strong_secret"
export NETFORGE_ADMIN_USER="ciadmin"
export NETFORGE_ADMIN_PASSWORD="ci_admin_long_unique_password"
export NETFORGE_KEY_DIRECTORY="$BASE/keys"

dotnet run --project selfhost/src/NetForge.Api/NetForge.Api.csproj -c Release --no-build >"$BASE/server.log" 2>&1 &
PID="$!"
BASEURL="http://127.0.0.1:18990"
for i in $(seq 1 80); do
  if curl -fsS "$BASEURL/health" -o "$BASE/result.json" 2>/dev/null; then break; fi
  sleep 0.5
done
if ! curl -fsS "$BASEURL/health" >/dev/null; then cat "$BASE/server.log"; exit 1; fi
loginStatus="$(curl -sS -D "$BASE/login.headers" -o "$BASE/result.json" -w "%{http_code}" -X POST "$BASEURL/api/login"   -H "Content-Type: application/json"   --data '{"username":"ciadmin","password":"ci_admin_long_unique_password"}')"
test "$loginStatus" = "200" || { cat "$BASE/result.json"; exit 1; }
COOKIE="$(grep -i "^set-cookie: NetForge.Auth=" "$BASE/login.headers" | head -1 | sed -E 's/^[^:]*: ([^;]*).*/\1/' | tr -d '\r')"
CSRF="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["csrf"])' "$BASE/result.json")"
test -n "$COOKIE"
test -n "$CSRF"
api() {
  local method="$1" endpoint="$2" payload="$3" expected="$4" status
  if [[ "$method" = "GET" ]]; then
    status="$(curl -sS -o "$BASE/result.json" -w "%{http_code}" "$BASEURL/api$endpoint"       -H "Cookie: $COOKIE")"
  else
    status="$(curl -sS -o "$BASE/result.json" -w "%{http_code}" -X "$method" "$BASEURL/api$endpoint"       -H "Cookie: $COOKIE" -H "X-CSRF-Token: $CSRF" -H "Content-Type: application/json" --data "$payload")"
  fi
  if [[ "$status" != "$expected" ]]; then
    echo "Expected $expected, got $status from $method $endpoint" >&2
    cat "$BASE/result.json" >&2
    exit 1
  fi
}
id() { python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["id"])' "$BASE/result.json"; }
api GET /status "" 200
api POST /branches '{"name":"Nelspruit"}' 201
BRANCH="$(id)"
api POST /highsites "{\"number\":\"HS-CI-1\",\"name\":\"North tower\",\"address\":\"CI tower\",\"branchId\":\"$BRANCH\",\"latitude\":-25.5,\"longitude\":31.0}" 201
SITE="$(id)"
api POST /sectors "{\"highsiteId\":\"$SITE\",\"name\":\"North 120\",\"ssid\":\"NF-CI\",\"azimuth\":120,\"frequencyMhz\":5800,\"maxCustomers\":60}" 201
SECTOR="$(id)"
api POST /customers "{\"account\":\"CI-001\",\"firstName\":\"Test\",\"surname\":\"Subscriber\",\"email\":\"ci@example.test\",\"phone\":\"\",\"address\":\"\",\"status\":\"active\",\"branchId\":\"$BRANCH\",\"highsiteId\":\"$SITE\",\"sectorId\":\"$SECTOR\"}" 201
CUSTOMER="$(id)"
api POST /packages '{"name":"20 Mbps WISP","downloadMbps":20,"uploadMbps":10,"monthlyPrice":499.00,"mikrotikRateLimit":"20M/10M"}' 201
PACKAGE="$(id)"
api POST /services "{\"customerId\":\"$CUSTOMER\",\"packageId\":\"$PACKAGE\",\"username\":\"ci-pppoe@example.test\",\"state\":\"pending\",\"staticIpv4\":null}" 201
SERVICE="$(id)"
api GET /services "" 200
api POST "/services/$SERVICE/credentials" '{"password":"CI-unique-subscriber-pass-2026"}' 200
api GET /services "" 200
grep -q 'radius_enabled' "$BASE/result.json" && grep -q 'true' "$BASE/result.json"
if grep -q 'CI-unique-subscriber-pass-2026' "$BASE/result.json"; then echo "RADIUS password leaked"; exit 1; fi
api POST /radius/nas '{"address":"192.0.2.1","name":"Test MikroTik","secret":"ci-very-long-unique-nas-secret","description":"Integration test"}' 201
api GET /radius/nas "" 200
if grep -q 'ci-very-long-unique-nas-secret' "$BASE/result.json"; then echo "NAS shared secret leaked"; exit 1; fi
api GET /radius/sessions "" 200
api POST /tickets "{\"customerId\":\"$CUSTOMER\",\"highsiteId\":\"$SITE\",\"subject\":\"Test customer outage\",\"description\":\"No connection\",\"type\":\"customer\",\"priority\":\"high\"}" 201
TICKET="$(id)"
api POST /billing/monthly-drafts '{"period":"2026-11-01"}' 200
grep -q '"created":1' "$BASE/result.json"
api POST /billing/monthly-drafts '{"period":"2026-11-01"}' 200
grep -q '"created":0' "$BASE/result.json"
api GET /invoices "" 200
INVOICE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[0]["id"])' "$BASE/result.json")"
api POST "/invoices/$INVOICE/issue" '{}' 200
api POST /payments "{\"invoiceId\":\"$INVOICE\",\"amount\":499,\"reference\":\"CI-EFT-001\"}" 200
api POST /payments "{\"invoiceId\":\"$INVOICE\",\"amount\":1,\"reference\":\"OVERPAY\"}" 409
api POST "/services/$SERVICE/suspend" '{}' 200
api GET /services "" 200
grep -q '"radius_enabled":false' "$BASE/result.json"
api GET /dashboard "" 200
api GET /audit "" 200

# New phase: service speed changes, suspend/restore, and opt-in collections policy.
api POST /packages '{"name":"50 Mbps WISP","downloadMbps":50,"uploadMbps":20,"monthlyPrice":699,"mikrotikRateLimit":""}' 201
FAST_PACKAGE="$(id)"
api PUT "/services/$SERVICE/package" "{\"packageId\":\"$FAST_PACKAGE\"}" 200
grep -q '"rateLimit":"20M/50M"' "$BASE/result.json"
api GET /billing/enforcement "" 200
grep -q '"enabled":false' "$BASE/result.json"
api POST "/services/$SERVICE/credentials" '{"password":"rotated-for-manual-suspension"}' 200
api GET /services "" 200
grep -q '"radius_enabled":false' "$BASE/result.json"
api POST "/services/$SERVICE/reactivate" '{}' 200
api GET /services "" 200
grep -q '"radius_enabled":true' "$BASE/result.json"
api POST /invoices "{\"customerId\":\"$CUSTOMER\",\"number\":\"CI-OVERDUE-2025\",\"total\":125,\"dueDate\":\"2025-01-01\"}" 201
OVERDUE="$(id)"
api POST "/invoices/$OVERDUE/issue" '{}' 200
api PUT /billing/enforcement '{"enabled":true,"graceDays":0,"restoreWhenPaid":true}' 200
api POST /billing/enforcement/run '{}' 200
grep -q '"suspended":1' "$BASE/result.json"
api GET /services "" 200
grep -q '"suspension_reason":"overdue"' "$BASE/result.json"
grep -q '"radius_enabled":false' "$BASE/result.json"
api POST /payments "{\"invoiceId\":\"$OVERDUE\",\"amount\":125,\"reference\":\"CI-SETTLED-2025\"}" 200
api POST /billing/enforcement/run '{}' 200
grep -q '"restored":1' "$BASE/result.json"
api GET /services "" 200
grep -q '"radius_enabled":true' "$BASE/result.json"
api PUT /billing/enforcement '{"enabled":false,"graceDays":7,"restoreWhenPaid":false}' 200
api GET "/radius/usage?period=2026-11" "" 200
grep -q '"period":"2026-11"' "$BASE/result.json"

# Disconnect destinations are exclusively saved administrator-owned NAS clients.
api POST /radius/nas '{"address":"127.0.0.1","name":"Loopback disconnect simulator","secret":"mock-radius-shared-secret-2026","description":"Test-only responder"}' 201
LOOP_NAS="$(id)"
api PUT "/radius/nas/$LOOP_NAS/settings" '{"vendor":"mikrotik","disconnectPort":19700,"disconnectEnabled":true}' 200
api GET /radius/nas-management "" 200
grep -q '"disconnect_port":19700' "$BASE/result.json"
if grep -q 'mock-radius-shared-secret-2026' "$BASE/result.json"; then echo "NAS secret leaked" >&2; exit 1; fi

PGCONTAINER="$(docker ps --filter ancestor=postgres:17 --format '{{.ID}}' | head -n1)"
test -n "$PGCONTAINER"
insert_session() {
  local unique="$1" acctid
  acctid="$(docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc \
    "INSERT INTO radacct(acctsessionid,acctuniqueid,username,nasipaddress,acctstarttime,acctupdatetime,acctstoptime)
     VALUES ('$unique','$unique','ci-pppoe@example.test','127.0.0.1',now()-interval '1 minute',now(),NULL)
     RETURNING radacctid;" | tail -n1)"
  test -n "$acctid"
  echo "$acctid"
}
MOCK_SESSION="$(insert_session 'NETFORGE-TEST-ACK')"
REQUEST_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
RADIUS_MOCK_SECRET=mock-radius-shared-secret-2026 python3 selfhost/tests/radius-mock.py \
  --port 19700 --mode ack > "$BASE/mock.log" 2>&1 &
MOCK_PID="$!"
sleep 0.25
api POST "/radius/sessions/$MOCK_SESSION/disconnect" "{\"requestId\":\"$REQUEST_ID\",\"reason\":\"CI controlled session reset\",\"confirm\":true}" 200
grep -q '"result":"ack"' "$BASE/result.json"
wait "$MOCK_PID"
MOCK_PID=""
api POST "/radius/sessions/$MOCK_SESSION/disconnect" "{\"requestId\":\"$REQUEST_ID\",\"reason\":\"CI controlled session reset\",\"confirm\":true}" 200
grep -q '"replayed":true' "$BASE/result.json"
INVALID_SESSION="$(insert_session 'NETFORGE-TEST-BAD')"
RADIUS_MOCK_SECRET=mock-radius-shared-secret-2026 python3 selfhost/tests/radius-mock.py \
  --port 19700 --mode invalid > "$BASE/mock-bad.log" 2>&1 &
MOCK_PID="$!"
sleep 0.25
REQUEST_ID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
api POST "/radius/sessions/$INVALID_SESSION/disconnect" "{\"requestId\":\"$REQUEST_ID\",\"reason\":\"CI verify response authenticator\",\"confirm\":true}" 200
grep -q '"result":"error"' "$BASE/result.json"
wait "$MOCK_PID"
MOCK_PID=""
api GET /radius/operations "" 200
if grep -q 'mock-radius-shared-secret-2026' "$BASE/result.json"; then echo "Secret leaked in operation history" >&2; exit 1; fi


# Portal identity is separate from staff and scoped to the enrolled customer.
api POST /customer-portal/accounts "{\"customerId\":\"$CUSTOMER\",\"username\":\"CI-PORTAL\",\"password\":\"CI-Customer-Portal-Password-2026\"}" 201
api GET /customer-portal/accounts "" 200
if grep -q 'password_hash' "$BASE/result.json"; then echo "Portal hash leaked"; exit 1; fi
api POST "/tickets/$TICKET/messages" '{"visibility":"public","body":"Public customer update"}' 201
api POST "/tickets/$TICKET/messages" '{"visibility":"internal","body":"Internal troubleshooting note"}' 201
PORTALURL="$BASEURL/api/portal"
PSTATUS="$(curl -sS -D "$BASE/portal.headers" -o "$BASE/result.json" -w '%{http_code}' \
  -H 'Content-Type: application/json' -X POST "$PORTALURL/login" \
  --data '{"username":"CI-PORTAL","password":"CI-Customer-Portal-Password-2026"}')"
test "$PSTATUS" = 200
PORTAL_COOKIE="$(grep -i '^set-cookie: NetForge.Portal=' "$BASE/portal.headers" |
  head -1 | sed -E 's/^[^:]*: ([^;]*).*/\1/' | tr -d '\r')"
PORTAL_CSRF="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["csrf"])' "$BASE/result.json")"
test -n "$PORTAL_COOKIE"
test -n "$PORTAL_CSRF"
api POST /customers '{"account":"CI-ISOLATED-B","firstName":"Second","surname":"Customer","email":"","phone":"","address":"","status":"active","branchId":null,"highsiteId":null,"sectorId":null}' 201
OTHER_CUSTOMER="$(id)"
api POST /tickets "{\"customerId\":\"$OTHER_CUSTOMER\",\"highsiteId\":null,\"subject\":\"Other customer confidential outage\",\"description\":\"Private second account details\",\"type\":\"customer\",\"priority\":\"normal\"}" 201
OTHER_TICKET="$(id)"
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
 -H "Cookie: $PORTAL_COOKIE" "$PORTALURL/tickets")"
test "$CODE" = 200
if grep -q 'Other customer confidential outage' "$BASE/result.json"; then
 echo "Cross-customer ticket leaked"; exit 1
fi
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
 -H "Cookie: $PORTAL_COOKIE" "$PORTALURL/tickets/$OTHER_TICKET/messages")"
test "$CODE" = 404
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -X POST \
 -H "Cookie: $PORTAL_COOKIE" -H "X-CSRF-Token: $PORTAL_CSRF" \
 -H 'Content-Type: application/json' \
 --data '{"subject":"Subscriber portal ticket","description":"Customer portal support request"}' \
 "$PORTALURL/tickets")"
test "$CODE" = 201
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Cookie: $PORTAL_COOKIE" "$PORTALURL/profile")"
test "$CODE" = 200
grep -q 'CI-001' "$BASE/result.json"
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Cookie: $PORTAL_COOKIE" "$PORTALURL/tickets/$TICKET/messages")"
test "$CODE" = 200
grep -q 'Public customer update' "$BASE/result.json"
if grep -q 'Internal troubleshooting note' "$BASE/result.json"; then
  echo "Staff-only notes leaked to subscribers"; exit 1
fi
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -X POST \
  -H "Cookie: $PORTAL_COOKIE" -H 'Content-Type: application/json' \
  --data '{"subject":"CSRF bypass","description":"Missing token"}' "$PORTALURL/tickets")"
test "$CODE" = 403
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Cookie: $PORTAL_COOKIE" "$BASEURL/api/customers")"
test "$CODE" = 401
api PUT "/customer-portal/accounts/$CUSTOMER" '{"action":"disable"}' 200
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Cookie: $PORTAL_COOKIE" "$PORTALURL/profile")"
test "$CODE" = 401
api PUT "/customer-portal/accounts/$CUSTOMER" '{"action":"enable"}' 200
api PUT "/customer-portal/accounts/$CUSTOMER" '{"action":"reset","password":"New-CI-Portal-Password-2026!"}' 200
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -X POST \
 -H 'Content-Type: application/json' \
 --data '{"username":"CI-PORTAL","password":"CI-Customer-Portal-Password-2026"}' \
 "$PORTALURL/login")"
test "$CODE" = 401


# Track genuine per-interval counters rather than treating all cumulative
# bytes as traffic in the month in which a session began.
PGCONTAINER="$(docker ps --filter ancestor=postgres:17 --format '{{.ID}}' | head -n1)"
test -n "$PGCONTAINER"
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
  INSERT INTO radacct(acctsessionid,acctuniqueid,username,nasipaddress,
     acctstarttime,acctupdatetime,acctinputoctets,acctoutputoctets)
  VALUES('CI-INTERIM','CI-INTERIM-UNIQUE','ci-pppoe@example.test','192.0.2.1',
    '2026-10-31 23:50:00+00','2026-11-01 00:10:00+00',2147483648,1073741824);
" >/dev/null
api GET '/radius/usage-reconciled?period=2026-10' "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
records=json.load(open(sys.argv[1]))['records']
r=next(x for x in records if x['username']=='ci-pppoe@example.test')
assert r['upload_bytes']==1073741824,r
assert r['download_bytes']==536870912,r
assert r['observed_intervals']==1,r
PY
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
  UPDATE radacct SET acctupdatetime='2026-11-01 00:15:00+00'
  WHERE acctuniqueid='CI-INTERIM-UNIQUE';
  UPDATE radacct SET acctupdatetime='2026-11-01 00:20:00+00',
    acctinputoctets=3221225472,acctoutputoctets=2147483648
  WHERE acctuniqueid='CI-INTERIM-UNIQUE';
" >/dev/null
api GET '/radius/usage-reconciled?period=2026-11' "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
r=next(x for x in json.load(open(sys.argv[1]))['records'] if x['username']=='ci-pppoe@example.test')
assert r['upload_bytes']==2147483648,r
assert r['download_bytes']==1610612736,r
assert r['observed_intervals']==2,r
PY
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
  UPDATE radacct SET acctupdatetime='2026-11-01 00:25:00+00',acctinputoctets=10
  WHERE acctuniqueid='CI-INTERIM-UNIQUE';
" >/dev/null
api GET '/radius/usage-reconciled?period=2026-11' "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
r=next(x for x in json.load(open(sys.argv[1]))['records'] if x['username']=='ci-pppoe@example.test')
assert r['upload_bytes']==2147483648,r
assert r['observed_intervals']==2,r
PY


# Financial journal invariants, idempotent payment posting, credit notes and
# manually reviewed bank statement reconciliation.
api POST /invoices "{\"customerId\":\"$CUSTOMER\",\"number\":\"CI-FIN-300\",\"total\":300,\"dueDate\":\"2026-12-10\"}" 201
FIN_INVOICE="$(id)"
api POST "/invoices/$FIN_INVOICE/issue" '{}' 200
FIN_REQUEST="$(python3 -c 'import uuid;print(uuid.uuid4())')"
api POST /payments "{\"invoiceId\":\"$FIN_INVOICE\",\"amount\":100,\"reference\":\"FIN-TRANSFER-A\",\"requestId\":\"$FIN_REQUEST\"}" 200
FIN_PAYMENT="$(value paymentId)"
api POST /payments "{\"invoiceId\":\"$FIN_INVOICE\",\"amount\":100,\"reference\":\"FIN-TRANSFER-A\",\"requestId\":\"$FIN_REQUEST\"}" 200
grep -q '"replayed":true' "$BASE/result.json"
api POST /payments "{\"invoiceId\":\"$FIN_INVOICE\",\"amount\":100,\"reference\":\"FIN-TRANSFER-A\"}" 409
api POST /billing/credits "{\"invoiceId\":\"$FIN_INVOICE\",\"number\":\"CI-CREDIT-50\",\"amount\":50,\"reason\":\"Compensation for service outage\"}" 201
api GET /invoices "" 200
python3 - "$BASE/result.json" "$FIN_INVOICE" <<'PY'
import json,sys
row=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert float(row['paid'])==100,row
assert float(row['credited'])==50,row
assert float(row['outstanding'])==150,row
PY
BANK_PREVIEW="$(python3 - <<'PY'
import json
print(json.dumps({"bankName":"CI-FNB","filename":"ci-statement.csv",
"csv":"Date,Amount,Description,Reference,TransactionId\\n2026-11-16,200.00,Client EFT,CI-001,CI-BANK-01\\n2026-11-16,-25.00,Bank charge,CHARGE,CI-BANK-02\\n2026-11-16,200.00,Duplicate,CI-001,CI-BANK-01\\n",
"confirm":False}))
PY
)"
api POST /billing/bank-imports/preview "$BANK_PREVIEW" 200
grep -q '"duplicateCount":1' "$BASE/result.json"
BANK_IMPORT="$(python3 - "$BANK_PREVIEW" <<'PY'
import json,sys
b=json.loads(sys.argv[1]);b['confirm']=True
print(json.dumps(b))
PY
)"
api POST /billing/bank-imports "$BANK_IMPORT" 200
grep -q '"imported":2' "$BASE/result.json"
api POST /billing/bank-imports "$BANK_IMPORT" 200
grep -q '"imported":0' "$BASE/result.json"
api GET /billing/bank-lines "" 200
BANK_LINE="$(python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
print(next(x['id'] for x in rows if x['transaction_id']=='CI-BANK-01'))
PY
)"
BANK_ALLOC_REQUEST="$(python3 -c 'import uuid;print(uuid.uuid4())')"
api POST "/billing/bank-lines/$BANK_LINE/allocate" "{\"invoiceId\":\"$FIN_INVOICE\",\"amount\":150,\"requestId\":\"$BANK_ALLOC_REQUEST\"}" 200
api POST "/billing/bank-lines/$BANK_LINE/allocate" "{\"invoiceId\":\"$FIN_INVOICE\",\"amount\":150,\"requestId\":\"$BANK_ALLOC_REQUEST\"}" 200
grep -q '"replayed":true' "$BASE/result.json"
api GET /invoices "" 200
python3 - "$BASE/result.json" "$FIN_INVOICE" <<'PY'
import json,sys
x=next(r for r in json.load(open(sys.argv[1])) if r['id']==sys.argv[2])
assert x['status']=='settled',x
assert float(x['outstanding'])==0,x
assert float(x['paid'])==250,x
assert float(x['credited'])==50,x
PY
api POST /invoices "{\"customerId\":\"$CUSTOMER\",\"number\":\"CI-FIN-SPLIT\",\"total\":60,\"dueDate\":\"2026-12-10\"}" 201
SPLIT_INVOICE="$(id)"
api POST "/invoices/$SPLIT_INVOICE/issue" '{}' 200
api POST "/billing/bank-lines/$BANK_LINE/allocate" "{\"invoiceId\":\"$SPLIT_INVOICE\",\"amount\":51,\"requestId\":\"$(python3 -c 'import uuid;print(uuid.uuid4())')\"}" 409
api POST "/billing/bank-lines/$BANK_LINE/allocate" "{\"invoiceId\":\"$SPLIT_INVOICE\",\"amount\":50,\"requestId\":\"$(python3 -c 'import uuid;print(uuid.uuid4())')\"}" 200
grep -q '"bankRemaining":0' "$BASE/result.json"
api GET /billing/bank-lines "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
row=next(x for x in json.load(open(sys.argv[1])) if x['transaction_id']=='CI-BANK-01')
assert float(row['allocated'])==200,row
assert float(row['available'])==0,row
PY
FIN_REVERSAL="$(python3 -c 'import uuid;print(uuid.uuid4())')"
api POST "/billing/payments/$FIN_PAYMENT/reverse" "{\"requestId\":\"$FIN_REVERSAL\",\"reason\":\"Confirmed bank payment reversal\"}" 200
api POST "/billing/payments/$FIN_PAYMENT/reverse" "{\"requestId\":\"$FIN_REVERSAL\",\"reason\":\"Confirmed bank payment reversal\"}" 200
grep -q '"replayed":true' "$BASE/result.json"
api GET /invoices "" 200
python3 - "$BASE/result.json" "$FIN_INVOICE" <<'PY'
import json,sys
x=next(r for r in json.load(open(sys.argv[1])) if r['id']==sys.argv[2])
assert x['status']=='issued',x
assert float(x['paid'])==150,x
assert float(x['credited'])==50,x
assert float(x['outstanding'])==100,x
PY
api GET /billing/trial-balance "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
debit=sum(round(float(x['debit'])*100) for x in rows)
credit=sum(round(float(x['credit'])*100) for x in rows)
assert debit==credit,(debit,credit)
PY
api GET /billing/ledger "" 200
grep -q 'payment_reversal' "$BASE/result.json"
api GET /billing/credits "" 200
grep -q 'CI-CREDIT-50' "$BASE/result.json"
api GET /billing/payments "" 200
grep -q '"reversal_id"' "$BASE/result.json"
# The database must refuse any mutation of existing journal lines.
if docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 \
  -c "UPDATE ledger_lines SET debit=1 WHERE line_no=1" >/dev/null 2>&1; then
  echo "Immutable journal was edited"; exit 1
fi

echo "NetForge .NET 11 / PostgreSQL API smoke passed."
