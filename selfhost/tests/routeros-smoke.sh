#!/usr/bin/env bash
# root/selfhost/tests/routeros-smoke.sh
# Sourced by the main disposable smoke test. HTTPS mock binds to a non-loopback
# runner address; production loopback restrictions are never disabled.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=routeros-ci' \
  -keyout "$BASE/ros.key" -out "$BASE/ros.crt" >/dev/null 2>&1
ROS_PIN="$(openssl x509 -in "$BASE/ros.crt" -outform DER | sha256sum | cut -d' ' -f1)"
python3 selfhost/tests/routeros-rest-mock.py "$NETFORGE_CI_ROUTEROS_IP" 19443 "$BASE/ros.crt" "$BASE/ros.key" &
ROS_PID=$!
# Parent smoke trap handles fixture cleanup on failure.
sleep 0.5
kill -0 "$ROS_PID"
api POST /equipment "{\"highsiteId\":\"$SITE\",\"category\":\"router\",\"name\":\"CI RouterOS\",\"model\":\"CCR\",\"managementIp\":\"$NETFORGE_CI_ROUTEROS_IP\"}" 201
ROS_EQUIPMENT="$(id)"
api POST /routeros/devices "{\"equipmentId\":\"$ROS_EQUIPMENT\",\"name\":\"CI RouterOS\",\"port\":19443,\"username\":\"ci-reader\",\"password\":\"ci-router-password-2026\",\"certificateSha256\":\"$ROS_PIN\",\"confirm\":true}" 201
ROS_ID="$(id)"
api POST "/routeros/devices/$ROS_ID/poll" '{"confirm":true}' 409
api POST "/routeros/devices/$ROS_ID/enabled" '{"enabled":true,"confirm":true}' 200
api POST "/routeros/devices/$ROS_ID/poll" '{"confirm":true}' 200
grep -q '"sectionsOk":10' "$BASE/result.json"
api GET "/routeros/devices/$ROS_ID" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
row=json.load(open(sys.argv[1]))
snapshot=json.loads(row["snapshot"])
assert snapshot["resource"]["rows"][0]["version"]=="7.19.3",row
assert "NEVER_STORE_THIS" not in json.dumps(row),row
assert "ci-router-password-2026" not in json.dumps(row),row
PY
api POST "/routeros/devices/$ROS_ID/poll" '{"confirm":true}' 429
api GET /routeros/devices "" 200
if grep -q 'ci-router-password-2026\|password_ciphertext' "$BASE/result.json"; then exit 1; fi
# Rotating to an incorrect pin must disable the profile and reject TLS.
api POST "/routeros/devices/$ROS_ID/credentials" '{"username":"ci-reader","password":"ci-router-password-2026","certificateSha256":"0000000000000000000000000000000000000000000000000000000000000000","confirm":true}' 200
api POST "/routeros/devices/$ROS_ID/poll" '{"confirm":true}' 409
api POST "/routeros/devices/$ROS_ID/enabled" '{"enabled":true,"confirm":true}' 200
api POST "/routeros/devices/$ROS_ID/poll" '{"confirm":true}' 200
grep -q '"status":"failed"' "$BASE/result.json"
api GET "/routeros/devices/$ROS_ID" "" 200
grep -q 'transport_or_tls' "$BASE/result.json"
api POST "/routeros/devices/$ROS_ID/enabled" '{"enabled":false,"confirm":true}' 200
kill "$ROS_PID"; wait "$ROS_PID" 2>/dev/null || true
ROS_PID=""
echo "RouterOS pinned HTTPS inventory smoke passed."
