#!/usr/bin/env bash
# root/selfhost/tests/staff-security-smoke.sh
# Sourced by smoke.sh after other fixtures. Preserve admin cookie and CSRF.
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
