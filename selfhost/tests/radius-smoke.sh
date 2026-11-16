#!/usr/bin/env bash
# root/selfhost/tests/radius-smoke.sh
set -Eeuo pipefail
cd selfhost
cp .env.example .env
trap 'docker compose down -v --remove-orphans || true; rm -f .env' EXIT
sed -i 's/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=CiDB_unique_password_123456789/' .env
sed -i 's/^NETFORGE_ADMIN_PASSWORD=.*/NETFORGE_ADMIN_PASSWORD=CiAdmin_unique_password_123456789/' .env
sed -i 's/^NETFORGE_HTTP_PORT=.*/NETFORGE_HTTP_PORT=18080/' .env
sed -i 's/^RADIUS_BIND_IP=.*/RADIUS_BIND_IP=127.0.0.1/' .env
docker compose up -d --build
for i in $(seq 1 70); do
  if curl -fsS http://127.0.0.1:18080/health >/dev/null 2>&1; then break; fi
  sleep 2
done
curl -fsS http://127.0.0.1:18080/health
docker compose ps
docker compose exec -T radius freeradius -XC >/tmp/netforge-radius-config.log 2>&1 || {
  cat /tmp/netforge-radius-config.log;docker compose logs radius;exit 1;
}
BASE="$(mktemp -d)"
trap 'docker compose logs radius | tail -50 || true; docker compose down -v --remove-orphans || true; rm -f .env; rm -rf "$BASE"' EXIT
URL=http://127.0.0.1:18080/api
status="$(curl -sS -D "$BASE/headers" -o "$BASE/result" -w '%{http_code}' -X POST "$URL/login" -H 'Content-Type: application/json' --data '{"username":"admin","password":"CiAdmin_unique_password_123456789"}')"
test "$status" = 200
cookie="$(grep -i '^set-cookie: NetForge.Auth=' "$BASE/headers" | head -1 | sed -E 's/^[^:]*: ([^;]*).*/\1/' | tr -d '\r')"
csrf="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["csrf"])' "$BASE/result")"
api() {
  local endpoint="$1" payload="$2" code
  code="$(curl -sS -o "$BASE/result" -w '%{http_code}' -X POST "$URL$endpoint" \
    -H "Cookie: $cookie" -H "X-CSRF-Token: $csrf" -H "Content-Type: application/json" --data "$payload")"
  if [[ "$code" != 201 && "$code" != 200 ]]; then
    echo "Request failed: $endpoint ($code)" >&2;cat "$BASE/result" >&2;exit 1
  fi
}
id(){ python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["id"])' "$BASE/result"; }
api /customers '{"account":"RADIUS-CI-001","firstName":"Radius","surname":"Subscriber","email":"","phone":"","address":"","status":"active","branchId":null,"highsiteId":null,"sectorId":null}'
customer="$(id)"
api /packages '{"name":"RADIUS CI profile","downloadMbps":20,"uploadMbps":10,"monthlyPrice":250,"mikrotikRateLimit":"20M/10M"}'
package="$(id)"
api /services "{\"customerId\":\"$customer\",\"packageId\":\"$package\",\"username\":\"rad-ci-user\",\"state\":\"pending\",\"staticIpv4\":null}"
service="$(id)"
api "/services/$service/credentials" '{"password":"RadiusCiUserPassword2026"}'
docker compose exec -T radius radtest rad-ci-user RadiusCiUserPassword2026 127.0.0.1 0 testing123 | tee "$BASE/authok"
grep -q 'Access-Accept' "$BASE/authok"
docker compose exec -T radius radtest rad-ci-user wrong-password 127.0.0.1 0 testing123 | tee "$BASE/authfail"
grep -q 'Access-Reject' "$BASE/authfail"
api "/services/$service/suspend" '{}'
docker compose exec -T radius radtest rad-ci-user RadiusCiUserPassword2026 127.0.0.1 0 testing123 | tee "$BASE/authsuspend"
grep -q 'Access-Reject' "$BASE/authsuspend"
echo 'Real FreeRADIUS SQL PAP accept/reject tests passed.'
