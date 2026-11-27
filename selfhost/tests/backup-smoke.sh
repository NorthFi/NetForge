#!/usr/bin/env bash
# root/selfhost/tests/backup-smoke.sh
# Sourced from the existing disposable PostgreSQL smoke harness.
api POST /backups/profiles "{\"equipmentId\":\"$ROS_EQUIPMENT\",\"name\":\"CI vault\",\"vendor\":\"routeros\",\"confirm\":true}" 201
VAULT="$(id)"
api POST "/backups/profiles/$VAULT/token" '{"enable":true,"confirm":true}' 200
VAULT_TOKEN="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["token"])' "$BASE/result.json")"
printf '/system identity\nset name=CI-Vault-Private\n' > "$BASE/backup.rsc"
manual_upload(){
 local expected="$1" file="$2" code
 code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
   -H "Cookie: $COOKIE" -H "X-CSRF-Token: $CSRF" -H 'Content-Type: application/octet-stream' \
   --data-binary "@$file" "$BASEURL/api/backups/profiles/$VAULT/upload?filename=backup.rsc&kind=text&confirm=true")"
 test "$code" = "$expected" || { cat "$BASE/result.json"; return 1; }
}
manual_upload 201 "$BASE/backup.rsc"
V1="$(id)"
manual_upload 200 "$BASE/backup.rsc"
grep -q '"duplicate":true' "$BASE/result.json"
printf '/system identity\nset name=CI-Vault-Changed\n' > "$BASE/changed.rsc"
manual_upload 201 "$BASE/changed.rsc"
V2="$(id)"
api GET "/backups/profiles/$VAULT/compare?older=$V1&newer=$V2" "" 200
grep -q '"olderChangedLines":1' "$BASE/result.json"
grep -q 'CI-Vault-Changed' "$BASE/result.json"
curl -fsS -H "Cookie: $COOKIE" "$BASEURL/api/backups/profiles/$VAULT/versions/$V1/download" -o "$BASE/restored.rsc"
cmp "$BASE/backup.rsc" "$BASE/restored.rsc"
api GET "/backups/profiles/$VAULT/versions" "" 200
if grep -q 'CI-Vault-Private\|ciphertext\|token_hash' "$BASE/result.json"; then exit 1; fi
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -Atqc \
 "SELECT position(convert_to('CI-Vault-Private','UTF8') in ciphertext) FROM backup_versions WHERE id='$V1'" | grep -qx '0'
# Token cannot read, and valid duplicate upload is safe to retry.
code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -H "Authorization: Bearer $VAULT_TOKEN" "$BASEURL/api/backups/profiles/$VAULT/versions")"
test "$code" = 401
collector_upload(){
 local expected="$1" code
 code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -H "Authorization: Bearer $VAULT_TOKEN" \
 -H 'Content-Type: application/octet-stream' --data-binary @"$BASE/changed.rsc" \
 "$BASEURL/api/backup-ingest/$VAULT?filename=changed.rsc&kind=text")"
 test "$code" = "$expected" || { cat "$BASE/result.json"; return 1; }
}
collector_upload 200
printf '/system identity\nset name=CI-Vault-Collector\n' > "$BASE/changed.rsc"
collector_upload 429
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c \
 "UPDATE backup_profiles SET last_received_at=now()-interval '61 seconds' WHERE id='$VAULT'" >/dev/null
collector_upload 201
api POST "/backups/profiles/$VAULT/token" '{"enable":false,"confirm":true}' 200
collector_upload 401
api GET /backups/profiles "" 200
if grep -q "$VAULT_TOKEN" "$BASE/result.json"; then exit 1; fi
echo "Encrypted backup vault smoke passed."
