#!/usr/bin/env bash
# root/selfhost/tests/network-changes-smoke.sh
# Uses existing admin, independent SLA approver and backup vault fixtures.
api POST /changes/scripts '{"name":"CI read-only inspection","vendor":"routeros","description":"Manual change fixture"}' 201
SCRIPT_ID="$(id)"
api POST "/changes/scripts/$SCRIPT_ID/versions" '{"body":"/system identity print","confirm":true}' 201
REV="$(id)"
api POST "/changes/scripts/$SCRIPT_ID/versions" '{"body":"/system resource print","confirm":true}' 201
REV2="$(id)"
api POST /equipment "{\"highsiteId\":\"$SITE\",\"category\":\"router\",\"name\":\"CI second target\",\"model\":\"CCR\",\"managementIp\":\"192.0.2.89\"}" 201
CHANGE_EQUIP="$(id)"
api POST /backups/profiles "{\"equipmentId\":\"$CHANGE_EQUIP\",\"name\":\"CI second vault\",\"vendor\":\"routeros\",\"confirm\":true}" 201
CHANGE_VAULT="$(id)"
api POST /changes/groups "{\"name\":\"CI change group\",\"profileIds\":[\"$VAULT\",\"$CHANGE_VAULT\"]}" 201
GROUP="$(id)"
START="$(date -u -d '5 minutes ago' +%Y-%m-%dT%H:%M:%SZ)"
END="$(date -u -d '1 hour' +%Y-%m-%dT%H:%M:%SZ)"
api POST /changes/plans "{\"versionId\":\"$REV\",\"groupId\":\"$GROUP\",\"canaryProfileId\":\"$VAULT\",\"reason\":\"CI manual verification\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\",\"confirm\":true}" 201
PLAN_ID="$(id)"
api GET "/changes/plans/$PLAN_ID/targets" "" 200
T1="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[0]["id"])' "$BASE/result.json")"
T2="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[1]["id"])' "$BASE/result.json")"
api POST "/changes/plans/$PLAN_ID/approve" '{"confirm":true}' 409
api POST "/changes/plans/$PLAN_ID/targets/$T1/handoff" '{"confirm":true}' 409
# Mutating the group after creation cannot change frozen plan targets.
api PUT "/changes/groups/$GROUP" "{\"profileIds\":[\"$VAULT\"],\"confirm\":true}" 200
api GET "/changes/plans/$PLAN_ID/targets" "" 200
python3 -c 'import json,sys;assert len(json.load(open(sys.argv[1])))==2' "$BASE/result.json"
CHANGE_COOKIE="$COOKIE"; CHANGE_CSRF="$CSRF"
COOKIE="$APPROVER_COOKIE"; CSRF="$APPROVER_CSRF"
api POST "/changes/plans/$PLAN_ID/approve" '{"confirm":true}' 200
COOKIE="$CHANGE_COOKIE"; CSRF="$CHANGE_CSRF"
api POST "/changes/plans/$PLAN_ID/targets/$T2/handoff" '{"confirm":true}' 409
api POST "/changes/plans/$PLAN_ID/targets/$T1/handoff" '{"confirm":true}' 200
grep -q '/system identity print' "$BASE/result.json"
if grep -q '/system resource print' "$BASE/result.json"; then exit 1; fi
api POST "/changes/plans/$PLAN_ID/targets/$T2/handoff" '{"confirm":true}' 409
api POST "/changes/plans/$PLAN_ID/targets/$T1/outcome" '{"success":true,"note":"Verified canary manually","confirm":true}' 200
api POST "/changes/plans/$PLAN_ID/targets/$T2/handoff" '{"confirm":true}' 409
# A fresh backup allows the next target. No network command is ever sent.
code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -H "Cookie: $COOKIE" -H "X-CSRF-Token: $CSRF" -H 'Content-Type: application/octet-stream' --data-binary @"$BASE/backup.rsc" "$BASEURL/api/backups/profiles/$CHANGE_VAULT/upload?filename=backup.rsc&kind=text&confirm=true")"
test "$code" = 201
api POST "/changes/plans/$PLAN_ID/targets/$T2/handoff" '{"confirm":true}' 200
api POST "/changes/plans/$PLAN_ID/targets/$T2/outcome" '{"success":false,"note":"Operator reported uncertainty","confirm":true}' 200
grep -q '"state":"halted"' "$BASE/result.json"
api POST "/changes/plans/$PLAN_ID/targets/$T2/handoff" '{"confirm":true}' 409
echo "Network change control smoke passed."
