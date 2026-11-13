# root/tests/api-smoke.sh
set -Eeuo pipefail

# Simulate the Afrihost directory layout so the API's private-store guard runs.
BASE="$(mktemp -d)"
PORT=28763
SERVER_PID=""
trap 'if [[ -n "$SERVER_PID" ]]; then kill "$SERVER_PID" 2>/dev/null || true; fi; rm -rf "$BASE"' EXIT

mkdir -p "$BASE/public_html/netforge/api"
cp api/index.php "$BASE/public_html/netforge/api/index.php"
php -S "127.0.0.1:$PORT" -t "$BASE/public_html" > "$BASE/php-server.log" 2>&1 &
SERVER_PID="$!"
URL="http://127.0.0.1:$PORT/netforge/api/index.php"

for iteration in 1 2 3 4 5 6 7 8 9 10; do
  if curl -fsS "$URL?action=status" > "$BASE/status.json" 2>/dev/null; then break; fi
  sleep 0.3
done
test -s "$BASE/status.json"

status="$(curl -sS -D "$BASE/headers" -o "$BASE/result.json" -w '%{http_code}' \
  -X POST "$URL?action=setup" \
  -H 'Content-Type: application/json' \
  --data '{"username":"ciadmin","password":"NETFORGE-ci-test-password-2026!"}')"
test "$status" = "201"
# php.ini enforces Secure cookies; for the HTTP-only loopback test send the
# session cookie explicitly. Real deployment must use HTTPS.
SID="$(sed -n 's/^[Ss]et-[Cc]ookie: NETFORGESESSID=\([^;]*\).*/\1/p' "$BASE/headers" | tr -d '\r' | tail -n 1)"
test -n "$SID"
CSRF="$(php -r '$j=json_decode(file_get_contents($argv[1]),true);echo $j["csrf"]??"";' "$BASE/result.json")"
test -n "$CSRF"

api() {
  local method="$1" query="$2" payload="${3:-}" expected="$4"
  local code
  if [[ "$method" = "GET" || "$method" = "DELETE" ]]; then
    code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
      -X "$method" "$URL?$query" \
      -H "Cookie: NETFORGESESSID=$SID" \
      -H "X-CSRF-Token: $CSRF")"
  else
    code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
      -X "$method" "$URL?$query" \
      -H "Cookie: NETFORGESESSID=$SID" \
      -H "X-CSRF-Token: $CSRF" \
      -H "Content-Type: application/json" \
      --data "$payload")"
  fi
  if [[ "$code" != "$expected" ]]; then
    echo "Expected HTTP $expected, got $code for $method $query" >&2
    cat "$BASE/result.json" >&2
    exit 1
  fi
}
value() {
  php -r '$j=json_decode(file_get_contents($argv[1]),true);echo $j[$argv[2]]??"";' "$BASE/result.json" "$1"
}

api POST category=highsites '{"name":"CI North Tower","number":"HS-001"}' 201
SITE="$(value id)"
api POST category=sectors "{\"name\":\"CI Sector 1\",\"highsite_id\":\"$SITE\",\"frequency\":\"5800\"}" 201
SECTOR="$(value id)"
api POST category=customers "{\"name\":\"Alice\",\"surname\":\"Ci\",\"account\":\"CI-001\",\"highsite_id\":\"$SITE\",\"sector_id\":\"$SECTOR\",\"account_status\":\"active\"}" 201
CUSTOMER="$(value id)"
test "$(value revision)" = "1"

api DELETE "category=sectors&id=$SECTOR" "" 409
api POST category=equipment "{\"name\":\"Test CPE\",\"customer_id\":\"$CUSTOMER\",\"category\":\"cpe\",\"ip\":\"192.0.2.10\"}" 201
api POST category=activities "{\"name\":\"First support call\",\"customer_id\":\"$CUSTOMER\",\"type\":\"call\",\"description\":\"Checked the POE.\"}" 201
test "$(value performed_by)" = "ciadmin"
api POST category=services "{\"name\":\"Wireless service\",\"customer_id\":\"$CUSTOMER\",\"highsite_id\":\"$SITE\",\"sector_id\":\"$SECTOR\"}" 201
api POST category=invoices "{\"name\":\"November service\",\"customer_id\":\"$CUSTOMER\",\"amount\":\"125.50\"}" 201
INVOICE="$(value id)"
api POST category=payments "{\"name\":\"Partial EFT\",\"customer_id\":\"$CUSTOMER\",\"invoice_id\":\"$INVOICE\",\"amount\":\"75.25\"}" 201
api POST category=link_checks "{\"name\":\"Install RSSI\",\"customer_id\":\"$CUSTOMER\",\"sector_id\":\"$SECTOR\",\"rssi_dbm\":\"-65\"}" 201
api POST category=surveys "{\"name\":\"Line-of-sight assessment\",\"highsite_id\":\"$SITE\",\"los_status\":\"clear\"}" 201
api POST category=workorders "{\"name\":\"Install router\",\"customer_id\":\"$CUSTOMER\",\"scheduled_date\":\"2026-11-14\"}" 201

api POST category=ipam '{"name":"Backhaul subnet","address":"192.0.2.0/24"}' 201
api POST category=ipam '{"name":"Duplicate subnet","address":"192.0.2.0/24"}' 409
api PUT category=customers "{\"id\":\"$CUSTOMER\",\"revision\":1,\"name\":\"Alice Updated\",\"account\":\"CI-001\",\"highsite_id\":\"$SITE\",\"sector_id\":\"$SECTOR\"}" 200
test "$(value revision)" = "2"
api PUT category=customers "{\"id\":\"$CUSTOMER\",\"revision\":1,\"name\":\"Stale edit\",\"account\":\"CI-001\",\"highsite_id\":\"$SITE\",\"sector_id\":\"$SECTOR\"}" 409

api GET category=customers "" 200
grep -q 'Alice Updated' "$BASE/result.json"
echo "NetForge API smoke tests passed."
