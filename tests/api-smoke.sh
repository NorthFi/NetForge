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

# Recurring billing: package price lookup, draft creation, repeated-run protection,
# immutable generated values, issue transition and payment authorization.
api POST category=packages '{"name":"CI 20 Mbps","price":"399.90","speed":"20M"}' 201
api POST category=services "{\"name\":\"CI monthly access\",\"status\":\"active\",\"customer_id\":\"$CUSTOMER\",\"highsite_id\":\"$SITE\",\"sector_id\":\"$SECTOR\",\"package\":\"CI 20 Mbps\"}" 201
PAID_SERVICE="$(value id)"
api GET "action=billing_preview&period=2026-11" "" 200
test "$(value ready_total)" = "399.90"
test "$(php -r '$j=json_decode(file_get_contents($argv[1]),true);echo count($j["ready"]);' "$BASE/result.json")" = "1"
TOKEN="$(value token)"
api POST action=billing_generate "{\"period\":\"2026-11\",\"token\":\"$TOKEN\"}" 201
test "$(value created)" = "1"
# Replaying a stale quote never duplicates a monthly invoice.
api POST action=billing_generate "{\"period\":\"2026-11\",\"token\":\"$TOKEN\"}" 409
api GET "action=billing_preview&period=2026-11" "" 200
test "$(php -r '$j=json_decode(file_get_contents($argv[1]),true);echo count($j["ready"]);' "$BASE/result.json")" = "0"
test "$(php -r '$j=json_decode(file_get_contents($argv[1]),true);echo count($j["existing"]);' "$BASE/result.json")" = "1"

api GET category=invoices "" 200
DRAFT="$(php -r '$j=json_decode(file_get_contents($argv[1]),true);foreach($j as $r){if(($r["billing_period"]??"")==="2026-11"){echo $r["id"];break;}}' "$BASE/result.json")"
test -n "$DRAFT"
api POST category=payments "{\"name\":\"Invalid early EFT\",\"customer_id\":\"$CUSTOMER\",\"invoice_id\":\"$DRAFT\",\"amount\":\"5\"}" 422
api POST action=invoice_issue "{\"id\":\"$DRAFT\",\"revision\":1}" 200
test "$(value status)" = "issued"
test "$(value revision)" = "2"
api POST category=payments "{\"name\":\"Real EFT\",\"customer_id\":\"$CUSTOMER\",\"invoice_id\":\"$DRAFT\",\"amount\":\"100.00\"}" 201
api POST action=invoice_void "{\"id\":\"$DRAFT\",\"revision\":2,\"reason\":\"Incorrectly generated invoice\"}" 409
api PUT category=invoices "{\"id\":\"$DRAFT\",\"revision\":2,\"name\":\"Tampered recurring invoice\",\"amount\":\"1\",\"status\":\"issued\"}" 409

# Helpdesk timeline and work logging.
api POST category=tickets "{\"name\":\"Check subscriber radio\",\"type\":\"customer\",\"status\":\"open\",\"priority\":\"high\",\"customer_id\":\"$CUSTOMER\"}" 201
TICKET="$(value id)"
api POST category=ticket_worklogs "{\"name\":\"Remote diagnosis\",\"ticket_id\":\"$TICKET\",\"minutes\":\"35\",\"description\":\"Checked signal\"}" 201
test "$(value performed_by)" = "ciadmin"
api POST category=ticket_worklogs "{\"name\":\"Invalid work duration\",\"ticket_id\":\"$TICKET\",\"minutes\":\"0\"}" 422
api POST category=todos "{\"name\":\"Phone customer tomorrow\",\"customer_id\":\"$CUSTOMER\",\"ticket_id\":\"$TICKET\",\"status\":\"open\",\"due_date\":\"2026-11-14\"}" 201
api DELETE "category=tickets&id=$TICKET" "" 409

# Manual bandwidth usage is linked to its service, without network enforcement.
api POST category=usage_records "{\"name\":\"November usage\",\"service_id\":\"$PAID_SERVICE\",\"customer_id\":\"$CUSTOMER\",\"period\":\"2026-11\",\"download_gb\":\"12.5\",\"upload_gb\":\"3.2\",\"cap_gb\":\"100\"}" 201
api POST category=usage_records "{\"name\":\"Bad service\",\"service_id\":\"ffffffffffffffffffffffffffffffff\",\"customer_id\":\"$CUSTOMER\",\"period\":\"2026-11\"}" 422

# Atomic stock movements with stale revision protection and idempotency keys.
api POST category=inventory '{"name":"CPE Flex Mount","sku":"NF-MOUNT","quantity":"10","minimum":"5"}' 201
STOCK="$(value id)"
KEY=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
api POST action=stock_adjust "{\"id\":\"$STOCK\",\"revision\":1,\"delta\":\"-3\",\"reason\":\"Allocated to customer CI-001\",\"request_id\":\"$KEY\"}" 200
test "$(value quantity)" = "7"
test "$(value revision)" = "2"
api POST action=stock_adjust "{\"id\":\"$STOCK\",\"revision\":1,\"delta\":\"-3\",\"reason\":\"Allocated to customer CI-001\",\"request_id\":\"$KEY\"}" 200
test "$(value duplicate)" = "1"
api POST action=stock_adjust "{\"id\":\"$STOCK\",\"revision\":1,\"delta\":\"-1\",\"reason\":\"Different movement\",\"request_id\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}" 409
api POST action=stock_adjust "{\"id\":\"$STOCK\",\"revision\":2,\"delta\":\"-999\",\"reason\":\"Invalid reduction\",\"request_id\":\"cccccccccccccccccccccccccccccccc\"}" 422
api PUT category=inventory "{\"id\":\"$STOCK\",\"revision\":2,\"name\":\"CPE Flex Mount\",\"sku\":\"NF-MOUNT\",\"quantity\":\"12\",\"minimum\":\"5\"}" 409
api PUT category=inventory "{\"id\":\"$STOCK\",\"revision\":2,\"name\":\"CPE Flex Mount\",\"sku\":\"NF-MOUNT\",\"quantity\":\"7\",\"minimum\":\"5\"}" 200
test "$(value revision)" = "3"
api GET category=inventory "" 200
test "$(php -r '$j=json_decode(file_get_contents($argv[1]),true);echo count($j[0]["movements"]??[]);' "$BASE/result.json")" = "1"

echo "NetForge API smoke tests passed."
