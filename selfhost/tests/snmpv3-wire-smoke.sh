#!/usr/bin/env bash
# root/selfhost/tests/snmpv3-wire-smoke.sh
# Temporary real Net-SNMP agent validates SHA-256/AES authPriv wire behavior.
set -Eeuo pipefail
BASE="$(mktemp -d)"
AGENT_PID=""
trap 'if [[ -n "$AGENT_PID" ]]; then kill "$AGENT_PID" 2>/dev/null || true; wait "$AGENT_PID" 2>/dev/null || true; fi; rm -rf "$BASE"' EXIT
mkdir -p "$BASE/agent" "$BASE/state" "$BASE/client"
chmod 700 "$BASE" "$BASE/agent" "$BASE/state" "$BASE/client"
cat > "$BASE/agent/snmpd.conf" <<'SNMPD'
agentAddress udp:127.0.0.1:1161
rouser ciSNMPv3 priv
sysLocation CI isolated lab
SNMPD
cat > "$BASE/state/snmpd.conf" <<'SNMPUSER'
createUser ciSNMPv3 SHA-256 "CI-AuthSecret-Strong-2026" AES "CI-PrivacySecret-Strong-2026"
SNMPUSER
cat > "$BASE/client/snmp.conf" <<'SNMPCONFIG'
defVersion 3
defSecurityLevel authPriv
defSecurityName ciSNMPv3
defAuthType SHA-256
defAuthPassphrase CI-AuthSecret-Strong-2026
defPrivType AES
defPrivPassphrase CI-PrivacySecret-Strong-2026
SNMPCONFIG
chmod 600 "$BASE/agent/snmpd.conf" "$BASE/state/snmpd.conf" "$BASE/client/snmp.conf"

# Use isolated environment directories, not the host's system SNMP users.
SNMPCONFPATH="$BASE/agent:$BASE/state" SNMP_PERSISTENT_DIR="$BASE/state" snmpd -f -Lo -c "$BASE/agent/snmpd.conf" > "$BASE/agent.log" 2>&1 &
AGENT_PID="$!"
ifIndex="$(cat /sys/class/net/lo/ifindex)"
test "$ifIndex" -ge 1
status=1
for i in $(seq 1 25); do
  if SNMPCONFPATH="$BASE/client" SNMP_PERSISTENT_DIR="$BASE/client"      snmpget -v 3 -l authPriv -u ciSNMPv3 -a SHA-256 -x AES -t 1 -r 0 -Oqv       'udp:127.0.0.1:1161'       '.1.3.6.1.2.1.1.3.0'       ".1.3.6.1.2.1.2.2.1.8.$ifIndex"       ".1.3.6.1.2.1.31.1.1.1.6.$ifIndex"       ".1.3.6.1.2.1.31.1.1.1.10.$ifIndex"       > "$BASE/response" 2> "$BASE/client.err"; then
    status=0; break
  fi
  sleep 0.2
done
if [[ "$status" -ne 0 ]]; then
  echo "SNMPv3 authPriv agent test failed. Inspect local runner SNMP configuration." >&2
  tail -n 12 "$BASE/client.err" | sed -E 's/(CI-AuthSecret|CI-PrivacySecret)[^ ]*/REDACTED/g' >&2
  exit 1
fi
python3 - "$BASE/response" <<'PY'
import sys,re
lines=open(sys.argv[1]).read().strip().splitlines()
assert len(lines)==4, lines
assert re.search(r'[0-9]+',lines[0]),lines
assert re.search(r'(up|1)',lines[1],re.I),lines
for count in lines[2:]:
    assert re.fullmatch(r'[0-9]+',count.strip()),lines
print('SNMPv3 SHA-256/AES authPriv wire test passed, four IF-MIB OIDs read.')
PY
