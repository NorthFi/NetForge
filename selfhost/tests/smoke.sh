#!/usr/bin/env bash
# root/selfhost/tests/smoke.sh
set -Eeuo pipefail
BASE="$(mktemp -d)"
PID=""
MOCK_PID=""
MONITOR_PID=""
ROS_PID=""
trap 'if [ -n "$ROS_PID" ]; then kill "$ROS_PID" 2>/dev/null || true; fi; if [ -n "$MONITOR_PID" ]; then kill "$MONITOR_PID" 2>/dev/null || true; fi; if [ -n "$MOCK_PID" ]; then kill "$MOCK_PID" 2>/dev/null || true; fi; if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; fi; cat "$BASE/server.log" 2>/dev/null | tail -35 || true; rm -rf "$BASE"' EXIT
export ASPNETCORE_URLS="http://127.0.0.1:18990"
export Database__ConnectionString="Host=127.0.0.1;Port=55432;Database=netforge_ci;Username=netforge_ci;Password=netforge_test_very_strong_secret"
export NETFORGE_ADMIN_USER="ciadmin"
export NETFORGE_ADMIN_PASSWORD="ci_admin_long_unique_password"
export NETFORGE_KEY_DIRECTORY="$BASE/keys"
# Deliberately unreachable test SMTP: confirms opt-in queuing and safe retries
# without delivering messages to real accounts.
export NETFORGE_SMTP_HOST="127.0.0.1"
export NETFORGE_SMTP_PORT="12587"
export NETFORGE_SMTP_FROM="notifications@example.test"
export NETFORGE_OPERATIONS_EMAIL="noc@example.test"
# The API invokes the mock through a trusted executable path. The mock verifies
# authPriv, protected configuration file permissions, and no secrets in argv.
export NETFORGE_CI_SNMP_MOCK="$PWD/selfhost/tests/snmpget-mock.py"
export NETFORGE_TEST_SNMP_COUNTER_FILE="$BASE/snmp-counter"
export NETFORGE_TEST_RF_COUNTER_FILE="$BASE/rf-counter"
cat >"$BASE/netforge-snmpget-ci" <<'SNMPWRAP'
#!/usr/bin/env bash
exec python3 "$NETFORGE_CI_SNMP_MOCK" "$@"
SNMPWRAP
chmod 700 "$BASE/netforge-snmpget-ci"
export NETFORGE_SNMPGET_PATH="$BASE/netforge-snmpget-ci"

# Use a specific non-loopback IPv4 host address and a /32 allowlist for CI only.
export NETFORGE_CI_ROUTEROS_IP="$(hostname -I | tr ' ' '\n' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' | head -n1)"
test -n "$NETFORGE_CI_ROUTEROS_IP"
export NETFORGE_ROUTEROS_ALLOWED_CIDRS="$NETFORGE_CI_ROUTEROS_IP/32"
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
FIN_PAYMENT="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["paymentId"])' "$BASE/result.json")"
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
"csv":"Date,Amount,Description,Reference,TransactionId\n2026-11-16,200.00,Client EFT,CI-001,CI-BANK-01\n2026-11-16,-25.00,Bank charge,CHARGE,CI-BANK-02\n2026-11-16,200.00,Client EFT,CI-001,CI-BANK-01\n",
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
api POST /billing/bank-imports '{"bankName":"CI-FNB","filename":"changed-statement.csv","confirm":true,"csv":"Date,Amount,Description,Reference,TransactionId\n2026-11-16,999.00,Changed bank credit,CI-001,CI-BANK-01\n"}' 409
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


# ------------------------------------------------------------------------
# Phase 5: opt-in billing schedule and queued notification integration.
# ------------------------------------------------------------------------
api GET /billing/schedule "" 200
grep -q '"enabled":false' "$BASE/result.json"
api GET /notifications/settings "" 200
grep -q '"customer_invoice_email":false' "$BASE/result.json"
api GET /notifications/outbox "" 200
test "$(cat "$BASE/result.json")" = '[]'
api POST /billing/schedule/run '{"period":"2027-01-01","confirm":true}' 200
grep -q '"enabled":false' "$BASE/result.json"

api GET '/billing/schedule/preview?period=2027-01' "" 200
grep -q '"period":"2027-01"' "$BASE/result.json"
api PUT /billing/schedule '{"enabled":true,"autoIssue":false,"runDay":1,"dueDays":7}' 200
api POST /billing/schedule/run '{"period":"2027-01-01","confirm":true}' 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
assert r["created"]>=1 and r["issued"]==0,r
PY
api GET /invoices "" 200
AUTO_DRAFT="$(python3 - "$BASE/result.json" "$CUSTOMER" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
print(next(x['id'] for x in rows if x['customer_id']==sys.argv[2] and x['period']=='2027-01-01'))
PY
)"
api GET "/billing/invoices/$AUTO_DRAFT/items" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
items=json.load(open(sys.argv[1]))
assert len(items)>=1,items
assert sum(float(x['amount']) for x in items)>0,items
PY
api POST /billing/schedule/run '{"period":"2027-01-01","confirm":true}' 200
grep -q '"created":0' "$BASE/result.json"

api PUT "/customers/$CUSTOMER/invoice-notifications" '{"enabled":true}' 200
api PUT /notifications/settings '{"customerInvoiceEmail":true,"opsEmail":true,"opsTelegram":false}' 200
api PUT /billing/schedule '{"enabled":true,"autoIssue":true,"runDay":1,"dueDays":7}' 200
api POST /billing/schedule/run '{"period":"2027-02-01","confirm":true}' 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
r=json.load(open(sys.argv[1]))
assert r["issued"]>=1 and r["created"]==r["issued"],r
PY
api GET /invoices "" 200
AUTO_ISSUED="$(python3 - "$BASE/result.json" "$CUSTOMER" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
r=next(x for x in rows if x['customer_id']==sys.argv[2] and x['period']=='2027-02-01')
assert r['status']=='issued',r
print(r['id'])
PY
)"

# Portal invoice line items must remain scoped to the authenticated customer.
api POST /invoices "{\"customerId\":\"$OTHER_CUSTOMER\",\"number\":\"CI-OTHER-INV\",\"total\":80,\"dueDate\":\"2027-03-01\"}" 201
OTHER_INVOICE="$(id)"
api POST "/invoices/$OTHER_INVOICE/issue" '{}' 200
PSTATUS="$(curl -sS -D "$BASE/portal-new.headers" -o "$BASE/result.json" -w '%{http_code}' \
  -H 'Content-Type: application/json' -X POST "$PORTALURL/login" \
  --data '{"username":"CI-PORTAL","password":"New-CI-Portal-Password-2026!"}')"
test "$PSTATUS" = 200
NEW_PORTAL_COOKIE="$(grep -i '^set-cookie: NetForge.Portal=' "$BASE/portal-new.headers" |
  head -1 | sed -E 's/^[^:]*: ([^;]*).*/\1/' | tr -d '\r')"
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Cookie: $NEW_PORTAL_COOKIE" "$PORTALURL/invoices/$AUTO_ISSUED/items")"
test "$CODE" = 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
items=json.load(open(sys.argv[1]))
assert len(items)>0 and float(items[0]['amount'])>0,items
PY
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
  -H "Cookie: $NEW_PORTAL_COOKIE" "$PORTALURL/invoices/$OTHER_INVOICE/items")"
test "$CODE" = 404

api GET /notifications/outbox "" 200
python3 - "$BASE/result.json" "$AUTO_ISSUED" <<'PY'
import json,sys
messages=json.load(open(sys.argv[1]))
assert any(x["channel"]=="email" and x["recipient"]=="ci@example.test" and "invoice" in x["subject"].lower() for x in messages),messages
PY
api POST /billing/schedule/run '{"period":"2027-02-01","confirm":true}' 200
grep -q '"created":0' "$BASE/result.json"
api GET /notifications/outbox "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
messages=json.load(open(sys.argv[1]))
assert len([x for x in messages if x["channel"]=="email" and "invoice" in x["subject"].lower()])==1,messages
PY
api GET /billing/schedule/runs "" 200
grep -q '2027-02-01' "$BASE/result.json"
api PUT /billing/schedule '{"enabled":false,"autoIssue":false,"runDay":1,"dueDays":7}' 200

# ------------------------------------------------------------------------
# Phase 5: only saved NAS or equipment can be probed; 2 failures -> DOWN;
# a successful TCP connection -> recovery; alerts are queued if enabled.
# ------------------------------------------------------------------------
api POST /monitor/targets '{"nasId":999999999,"equipmentId":null,"name":"Invalid NAS probe","tcpPort":19092,"enabled":true,"intervalSeconds":300,"failureThreshold":2,"confirm":true}' 409
api POST /monitor/targets "{\"nasId\":$LOOP_NAS,\"equipmentId\":null,\"name\":\"CI test NAS TCP\",\"tcpPort\":19092,\"enabled\":true,\"intervalSeconds\":300,\"failureThreshold\":2,\"confirm\":true}" 201
MONITOR_ID="$(id)"
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
api GET /monitor/targets "" 200
python3 - "$BASE/result.json" "$MONITOR_ID" <<'PY'
import json,sys
row=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert row['state']=='down',row
assert row['failure_streak']>=2,row
PY
api GET /monitor/events "" 200
grep -q '"transition":"down"' "$BASE/result.json"
api GET /notifications/outbox "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
messages=json.load(open(sys.argv[1]))
assert any(x['subject']=='NetForge network alert' and x['recipient']=='noc@example.test' for x in messages),messages
PY

python3 -m http.server 19092 --bind 127.0.0.1 > "$BASE/monitor-http.log" 2>&1 &
MONITOR_PID="$!"
sleep 0.3
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
grep -q '"transition":"recovered"' "$BASE/result.json"
api GET /monitor/events "" 200
grep -q '"transition":"recovered"' "$BASE/result.json"
kill "$MONITOR_PID" 2>/dev/null || true
MONITOR_PID=""
api PUT "/monitor/targets/$MONITOR_ID" '{"enabled":false,"intervalSeconds":300,"failureThreshold":2,"confirm":false}' 200
api GET /monitor/targets "" 200
python3 - "$BASE/result.json" "$MONITOR_ID" <<'PY'
import json,sys
row=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert row['enabled'] is False,row
PY
api PUT /notifications/settings '{"customerInvoiceEmail":false,"opsEmail":false,"opsTelegram":false}' 200


# ------------------------------------------------------------------------
# Phase 6: highsite correlation, suppression and SNMPv3 encrypted profiles.
# ------------------------------------------------------------------------
api GET /monitor/sites "" 200
api GET /monitor/site-events "" 200
api PUT "/monitor/targets/$MONITOR_ID/site" "{\"highsiteId\":\"$SITE\",\"isUpstream\":true,\"confirm\":true}" 200
api GET /monitor/targets "" 200
python3 - "$BASE/result.json" "$MONITOR_ID" "$SITE" <<'PY'
import json,sys
r=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert r['highsite_id']==sys.argv[3] and r['is_upstream'] is True,r
assert r['state']=='unknown' and r['failure_streak']==0,r
PY

# Real operational notification credentials remain the intentionally
# unreachable SMTP fixture; nothing leaves this runner.
api PUT /notifications/settings '{"customerInvoiceEmail":false,"opsEmail":true,"opsTelegram":false}' 200
api PUT "/monitor/targets/$MONITOR_ID" '{"enabled":true,"intervalSeconds":300,"failureThreshold":2,"confirm":true}' 200
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
api GET /monitor/sites "" 200
python3 - "$BASE/result.json" "$SITE" <<'PY'
import json,sys
r=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert int(r['upstream_down'])==1,r
assert r['impact_state']=='potential_upstream_outage',r
assert int(r['affected_customers'])>=1 and int(r['sectors'])>=1,r
PY
api GET /monitor/site-events "" 200
python3 - "$BASE/result.json" "$SITE" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert any(x['highsite_id']==sys.argv[2] and x['transition']=='down' for x in rows),rows
PY
api GET /notifications/outbox "" 200
SITE_ALERT_COUNT="$(python3 - "$BASE/result.json" <<'PY'
import json,sys
print(sum(1 for x in json.load(open(sys.argv[1])) if x['subject']=='NetForge network alert'))
PY
)"
api GET /monitor/events "" 200
python3 - "$BASE/result.json" "$MONITOR_ID" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert any(x['target_id']==sys.argv[2] and x['transition']=='down' and x['suppressed'] is True for x in rows),rows
PY

# The child monitor fails while the upstream is DOWN. Its event remains
# queryable but must not create another operational email.
api POST /monitor/targets "{\"nasId\":$LOOP_NAS,\"equipmentId\":null,\"name\":\"CI child access TCP\",\"tcpPort\":19093,\"enabled\":true,\"intervalSeconds\":300,\"failureThreshold\":2,\"confirm\":true}" 201
CHILD_MONITOR="$(id)"
api PUT "/monitor/targets/$CHILD_MONITOR/site" "{\"highsiteId\":\"$SITE\",\"isUpstream\":false,\"confirm\":true}" 200
api POST "/monitor/targets/$CHILD_MONITOR/check" '{}' 200
api POST "/monitor/targets/$CHILD_MONITOR/check" '{}' 200
api GET /monitor/events "" 200
python3 - "$BASE/result.json" "$CHILD_MONITOR" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert any(x['target_id']==sys.argv[2] and x['transition']=='down' and x['suppressed'] is True for x in rows),rows
PY
api GET /notifications/outbox "" 200
python3 - "$BASE/result.json" "$SITE_ALERT_COUNT" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
alerts=sum(1 for x in rows if x['subject']=='NetForge network alert')
assert alerts==int(sys.argv[2]),(alerts,sys.argv[2])
PY

# Device identity and credentials must never appear as unencrypted values
# in staff listing results or the PostgreSQL ciphertext columns.
api POST /snmp/devices '{"nasId":999999999,"equipmentId":null,"highsiteId":null,"name":"Invalid SNMP source","username":"CIv3","authProtocol":"SHA-256","authPassword":"CI-AuthSecret-Strong-2026","privacyPassword":"CI-PrivacySecret-Strong-2026","udpPort":161,"interfaceIndex":1,"intervalSeconds":300,"enabled":false,"confirm":false}' 409
api POST /snmp/devices "{\"nasId\":$LOOP_NAS,\"equipmentId\":null,\"highsiteId\":\"$SITE\",\"name\":\"CI tower SNMP\",\"username\":\"CIv3\",\"authProtocol\":\"SHA-256\",\"authPassword\":\"CI-AuthSecret-Strong-2026\",\"privacyPassword\":\"CI-PrivacySecret-Strong-2026\",\"udpPort\":161,\"interfaceIndex\":1,\"intervalSeconds\":300,\"enabled\":false,\"confirm\":false}" 201
SNMP_DEVICE="$(id)"
api GET /snmp/devices "" 200
python3 - "$BASE/result.json" "$SNMP_DEVICE" <<'PY'
import json,sys
text=open(sys.argv[1]).read()
assert "CI-AuthSecret-Strong-2026" not in text
assert "CI-PrivacySecret-Strong-2026" not in text
assert "secret_ciphertext" not in text
rows=json.loads(text)
assert any(r['id']==sys.argv[2] and r['enabled'] is False for r in rows),rows
PY
api POST "/snmp/devices/$SNMP_DEVICE/poll" '{}' 200
grep -q '"ok":false' "$BASE/result.json"
api PUT "/snmp/devices/$SNMP_DEVICE" '{"enabled":true,"intervalSeconds":300,"interfaceIndex":1,"confirm":true}' 200

for i in 1 2 3; do api POST "/snmp/devices/$SNMP_DEVICE/poll" '{}' 200; done
api GET "/snmp/devices/$SNMP_DEVICE/history?hours=24" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)==3,rows
assert rows[0]['input_bps'] is None and rows[0]['counter_reset'] is False,rows
assert float(rows[1]['input_bps'])>0 and float(rows[1]['output_bps'])>0,rows
assert rows[2]['counter_reset'] is True and rows[2]['input_bps'] is None,rows
PY
# The profile's USM credentials must be encrypted at rest. The DataProtection
# key directory is persisted independently via the normal Docker key volume.
PSQL="$PGCONTAINER"
docker exec "$PSQL" psql -U netforge_ci -d netforge_ci -Atqc \
  "SELECT position('CI-AuthSecret' in auth_secret_ciphertext),
          position('CI-PrivacySecret' in priv_secret_ciphertext)
   FROM snmp_devices WHERE id='$SNMP_DEVICE';" | python3 -c "import sys; assert sys.stdin.read().strip() == '0|0'"


# Subsequent SNMPv3 IF-MIB status=2 polls trigger a three-failure DOWN.
# Since upstream TCP is DOWN, this separate SNMP alert is suppressed.
for i in 4 5 6; do api POST "/snmp/devices/$SNMP_DEVICE/poll" '{}' 200; done
grep -q '"state":"down"' "$BASE/result.json"
api GET /snmp/events "" 200
python3 - "$BASE/result.json" "$SNMP_DEVICE" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert any(r['device_id']==sys.argv[2] and r['transition']=='down' and r['suppressed'] is True for r in rows),rows
PY

# The upstream TCP service becomes reachable: emit ONE site recovery.
python3 -m http.server 19092 --bind 127.0.0.1 > "$BASE/monitor-http-2.log" 2>&1 &
MONITOR_PID="$!"
sleep 0.35
api POST "/monitor/targets/$MONITOR_ID/check" '{}' 200
grep -q '"transition":"recovered"' "$BASE/result.json"
api GET /monitor/site-events "" 200
python3 - "$BASE/result.json" "$SITE" <<'PY'
import json,sys
rows=[r for r in json.load(open(sys.argv[1])) if r['highsite_id']==sys.argv[2]]
assert len([r for r in rows if r['transition']=='down'])==1,rows
assert len([r for r in rows if r['transition']=='recovered'])==1,rows
PY
api GET /monitor/sites "" 200
python3 - "$BASE/result.json" "$SITE" <<'PY'
import json,sys
r=next(r for r in json.load(open(sys.argv[1])) if r['id']==sys.argv[2])
assert int(r['upstream_down'])==0,r
PY

# The next SNMPv3 sample is healthy. It must record a non-suppressed
# recovery event because no site upstream TCP monitor remains down.
api POST "/snmp/devices/$SNMP_DEVICE/poll" '{}' 200
grep -q '"state":"up"' "$BASE/result.json"
api GET /snmp/events "" 200
python3 - "$BASE/result.json" "$SNMP_DEVICE" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert any(r['device_id']==sys.argv[2] and r['transition']=='recovered' and r['suppressed'] is False for r in rows),rows
PY
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
 -H "Cookie: $NEW_PORTAL_COOKIE" "$BASEURL/api/snmp/devices")"
test "$CODE" = 401

api PUT "/snmp/devices/$SNMP_DEVICE" '{"enabled":false,"intervalSeconds":300,"interfaceIndex":1,"confirm":false}' 200
api PUT "/monitor/targets/$MONITOR_ID" '{"enabled":false,"intervalSeconds":300,"failureThreshold":2,"confirm":false}' 200
api PUT "/monitor/targets/$CHILD_MONITOR" '{"enabled":false,"intervalSeconds":300,"failureThreshold":2,"confirm":false}' 200
kill "$MONITOR_PID" 2>/dev/null || true
MONITOR_PID=""
api PUT /notifications/settings '{"customerInvoiceEmail":false,"opsEmail":false,"opsTelegram":false}' 200

# Phase 7 extends existing lab profile and PostgreSQL fixture.
source selfhost/tests/rf-smoke.sh
source selfhost/tests/noc-smoke.sh
source selfhost/tests/assurance-smoke.sh
source selfhost/tests/sla-probes-smoke.sh
source selfhost/tests/subscriber-probes-smoke.sh
source selfhost/tests/ipam-smoke.sh
source selfhost/tests/routeros-smoke.sh
source selfhost/tests/backup-smoke.sh
source selfhost/tests/network-changes-smoke.sh
source selfhost/tests/network-execution-smoke.sh
source selfhost/tests/compliance-smoke.sh
source selfhost/tests/sales-smoke.sh
source selfhost/tests/warehouse-smoke.sh
source selfhost/tests/procurement-smoke.sh
source selfhost/tests/lifecycle-smoke.sh

echo "NetForge .NET 11 / PostgreSQL API smoke passed."
