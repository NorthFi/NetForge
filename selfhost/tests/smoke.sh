#!/usr/bin/env bash
# root/selfhost/tests/smoke.sh
set -Eeuo pipefail
BASE="$(mktemp -d)"
PID=""
trap 'if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; fi; cat "$BASE/server.log" 2>/dev/null | tail -35 || true; rm -rf "$BASE"' EXIT
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
echo "NetForge .NET 11 / PostgreSQL API smoke passed."
