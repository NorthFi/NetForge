#!/usr/bin/env bash
# root/selfhost/tests/staff-security-smoke.sh
# Run in a fresh process so the existing six-attempt login limiter is not
# weakened and the main smoke suite's staff/portal requests do not consume it.
set -Eeuo pipefail
BASE="$(mktemp -d)"
PID=""
trap 'if [ -n "$PID" ]; then kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true; fi; tail -35 "$BASE/server.log" 2>/dev/null || true; rm -rf "$BASE"' EXIT
export ASPNETCORE_URLS="http://127.0.0.1:18991"
export Database__ConnectionString="Host=127.0.0.1;Port=55432;Database=netforge_ci;Username=netforge_ci;Password=netforge_test_very_strong_secret"
export NETFORGE_ADMIN_USER="ciadmin"
export NETFORGE_ADMIN_PASSWORD="ci_admin_long_unique_password"
export NETFORGE_KEY_DIRECTORY="$BASE/keys"
BASEURL="$ASPNETCORE_URLS"
dotnet selfhost/src/NetForge.Api/bin/Release/net11.0/NetForge.Api.dll >"$BASE/server.log" 2>&1 &
PID="$!"
for i in $(seq 1 80); do
  if curl -fsS "$BASEURL/health" >/dev/null 2>&1; then break; fi
  sleep 0.5
done
curl -fsS "$BASEURL/health" >/dev/null
code="$(curl -sS -D "$BASE/admin.headers" -o "$BASE/result.json" -w '%{http_code}' \
  -H 'Content-Type: application/json' --data '{"username":"ciadmin","password":"ci_admin_long_unique_password"}' "$BASEURL/api/login")"
test "$code" = 200
COOKIE="$(grep -i '^set-cookie: NetForge.Auth=' "$BASE/admin.headers" | head -1 | sed -E 's/^[^:]*: ([^;]*).*/\1/' | tr -d '\r')"
CSRF="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["csrf"])' "$BASE/result.json")"
test -n "$COOKIE"
test -n "$CSRF"
api() {
  local method="$1" path="$2" payload="$3" expected="$4" code
  if [[ "$method" = GET ]]; then
    code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -H "Cookie: $COOKIE" "$BASEURL/api$path")"
  else
    code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -X "$method" \
      -H "Cookie: $COOKIE" -H "X-CSRF-Token: $CSRF" \
      -H 'Content-Type: application/json' --data "$payload" "$BASEURL/api$path")"
  fi
  test "$code" = "$expected" || { echo "Expected $expected got $code: $method $path"; cat "$BASE/result.json"; return 1; }
}
id() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["id"])' "$BASE/result.json"; }
api POST /staff '{"username":"ci_staff_security","password":"long_staff_password_ci_1","role":"technician"}' 201
STAFF_ID="$(id)"
api POST /staff '{"username":"CI_STAFF_SECURITY","password":"long_staff_password_ci_2","role":"viewer"}' 409
staff_login() {
  local expected="$1" password="$2" code
  code="$(curl -sS -D "$BASE/staff.headers" -o "$BASE/staff.json" -w '%{http_code}' \
    -H 'Content-Type: application/json' --data "{\"username\":\"ci_staff_security\",\"password\":\"$password\"}" "$BASEURL/api/login")"
  test "$code" = "$expected"
  if [[ "$expected" = 200 ]]; then
    STAFF_COOKIE="$(grep -i '^set-cookie: NetForge.Auth=' "$BASE/staff.headers" | head -1 | sed -E 's/^[^:]*: ([^;]*).*/\1/' | tr -d '\r')"
    STAFF_CSRF="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["csrf"])' "$BASE/staff.json")"
    test -n "$STAFF_COOKIE"
  fi
}
staff_request() {
  local expected="$1" method="$2" path="$3" data="${4:-}" code
  code="$(curl -sS -o "$BASE/staff.json" -w '%{http_code}' -X "$method" \
    -H "Cookie: $STAFF_COOKIE" -H "X-CSRF-Token: $STAFF_CSRF" \
    -H 'Content-Type: application/json' --data "$data" "$BASEURL/api$path")"
  test "$code" = "$expected" || { cat "$BASE/staff.json"; return 1; }
}
staff_login 200 long_staff_password_ci_1
staff_request 200 GET /status
staff_request 403 POST "/staff/$STAFF_ID/revoke-sessions" '{"confirm":true}'
api POST "/staff/$STAFF_ID/access" '{"role":"viewer","enabled":true,"confirm":false}' 400
api POST "/staff/$STAFF_ID/access" '{"role":"viewer","enabled":true,"confirm":true}' 200
staff_request 401 GET /status
staff_login 200 long_staff_password_ci_1
grep -q '"role":"viewer"' "$BASE/staff.json"
api POST "/staff/$STAFF_ID/password" '{"password":"long_staff_password_ci_2","confirm":true}' 200
staff_request 401 GET /status
staff_login 200 long_staff_password_ci_2
staff_request 200 POST /logout '{}'
# Replay the original cookie, not the browser's deletion cookie.
staff_request 401 GET /status
staff_login 200 long_staff_password_ci_2
api POST "/staff/$STAFF_ID/revoke-sessions" '{"confirm":true}' 200
staff_request 401 GET /status
staff_login 200 long_staff_password_ci_2
api POST "/staff/$STAFF_ID/access" '{"role":"viewer","enabled":false,"confirm":true}' 200
staff_request 401 GET /status
api GET /staff "" 200
ADMIN_ID="$(python3 -c 'import json,sys;print(next(x["id"] for x in json.load(open(sys.argv[1])) if x["username"]=="ciadmin"))' "$BASE/result.json")"
api POST "/staff/$ADMIN_ID/access" '{"role":"viewer","enabled":false,"confirm":true}' 400
echo "Staff session lifecycle smoke passed."
