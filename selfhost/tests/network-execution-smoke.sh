#!/usr/bin/env bash
# root/selfhost/tests/network-execution-smoke.sh
# Simulate the HTTPS agent API only: never send commands to real devices.
api POST "/changes/agents/$VAULT/token" '{"enable":true,"confirm":true}' 200
EXEC_TOKEN="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["token"])' "$BASE/result.json")"
api POST /changes/plans "{\"versionId\":\"$REV\",\"groupId\":\"$GROUP\",\"canaryProfileId\":\"$VAULT\",\"reason\":\"CI execution agent\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\",\"confirm\":true}" 201
EXEC_PLAN="$(id)"
api GET "/changes/plans/$EXEC_PLAN/targets" "" 200
EXEC_TARGET="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[0]["id"])' "$BASE/result.json")"
COOKIE="$APPROVER_COOKIE"; CSRF="$APPROVER_CSRF"
api POST "/changes/plans/$EXEC_PLAN/approve" '{"confirm":true}' 200
COOKIE="$CHANGE_COOKIE"; CSRF="$CHANGE_CSRF"
api POST "/changes/plans/$EXEC_PLAN/targets/$EXEC_TARGET/handoff" '{"confirm":true,"automatic":true}' 200
grep -q '"queued":true' "$BASE/result.json"
if grep -q '/system identity print' "$BASE/result.json"; then exit 1; fi
agent_api(){
 local token="$1" path="$2" body="$3" expected="$4" code
 code="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' -H "Authorization: Bearer $token" -H 'Content-Type: application/json' --data "$body" "$BASEURL/api/execution-agent/$VAULT$path")"
 test "$code" = "$expected" || { cat "$BASE/result.json"; return 1; }
}
agent_api "$VAULT_TOKEN" /claim "{\"address\":\"$NETFORGE_CI_ROUTEROS_IP\"}" 401
agent_api "$EXEC_TOKEN" /claim '{"address":"192.0.2.250"}' 409
agent_api "$EXEC_TOKEN" /claim "{\"address\":\"$NETFORGE_CI_ROUTEROS_IP\"}" 200
grep -q '/system identity print' "$BASE/result.json"
CLAIM="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["claim"])' "$BASE/result.json")"
agent_api "$EXEC_TOKEN" /claim "{\"address\":\"$NETFORGE_CI_ROUTEROS_IP\"}" 204
api POST "/changes/agents/$VAULT/token" '{"enable":false,"confirm":true}' 409
api POST "/changes/plans/$EXEC_PLAN/targets/$EXEC_TARGET/outcome" '{"success":true,"note":"Not yet reported","confirm":true}' 409
agent_api "$EXEC_TOKEN" "/jobs/$EXEC_TARGET/report" "{\"claim\":\"$CLAIM\",\"result\":\"exit_zero\",\"outputBytes\":10,\"exitCode\":0}" 200
agent_api "$EXEC_TOKEN" "/jobs/$EXEC_TARGET/report" "{\"claim\":\"$CLAIM\",\"result\":\"exit_zero\",\"outputBytes\":10,\"exitCode\":0}" 200
grep -q '"duplicate":true' "$BASE/result.json"
api POST "/changes/plans/$EXEC_PLAN/targets/$EXEC_TARGET/outcome" '{"success":true,"note":"Verified manually in fixture","confirm":true}' 200
grep -q '"state":"completed"' "$BASE/result.json"
# A failed SSH result cannot be attested as success, and blocks subsequent work.
api POST /changes/plans "{\"versionId\":\"$REV2\",\"groupId\":\"$GROUP\",\"canaryProfileId\":\"$VAULT\",\"reason\":\"CI failed execution agent\",\"windowStart\":\"$START\",\"windowEnd\":\"$END\",\"confirm\":true}" 201
FAIL_PLAN="$(id)"
api GET "/changes/plans/$FAIL_PLAN/targets" "" 200
FAIL_TARGET="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[0]["id"])' "$BASE/result.json")"
COOKIE="$APPROVER_COOKIE"; CSRF="$APPROVER_CSRF"
api POST "/changes/plans/$FAIL_PLAN/approve" '{"confirm":true}' 200
COOKIE="$CHANGE_COOKIE"; CSRF="$CHANGE_CSRF"
api POST "/changes/plans/$FAIL_PLAN/targets/$FAIL_TARGET/handoff" '{"confirm":true,"automatic":true}' 200
agent_api "$EXEC_TOKEN" /claim "{\"address\":\"$NETFORGE_CI_ROUTEROS_IP\"}" 200
FAIL_CLAIM="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["claim"])' "$BASE/result.json")"
agent_api "$EXEC_TOKEN" "/jobs/$FAIL_TARGET/report" "{\"claim\":\"$CLAIM\",\"result\":\"exit_zero\",\"outputBytes\":0,\"exitCode\":0}" 401
agent_api "$EXEC_TOKEN" "/jobs/$FAIL_TARGET/report" "{\"claim\":\"$FAIL_CLAIM\",\"result\":\"timeout\",\"outputBytes\":5,\"exitCode\":null}" 200
api POST "/changes/plans/$FAIL_PLAN/targets/$FAIL_TARGET/outcome" '{"success":true,"note":"False success must be denied","confirm":true}' 409
api POST "/changes/plans/$FAIL_PLAN/targets/$FAIL_TARGET/outcome" '{"success":false,"note":"Timeout; verified device manually","confirm":true}' 200
grep -q '"state":"halted"' "$BASE/result.json"
api POST "/changes/agents/$VAULT/token" '{"enable":false,"confirm":true}' 200
agent_api "$EXEC_TOKEN" /claim "{\"address\":\"$NETFORGE_CI_ROUTEROS_IP\"}" 401
echo "Network execution API smoke passed."
