#!/usr/bin/env bash
# root/selfhost/tests/rf-smoke.sh
# Sourced by smoke.sh: uses the logged-in staff API and its temporary test NAS.
# Synthetic numeric enterprise OIDs below are CI fixtures, NOT vendor MIBs.
set -Eeuo pipefail
api GET /telemetry/metrics "" 200
test "$(cat "$BASE/result.json")" = '[]'
api GET /telemetry/retention "" 200
grep -q '"enabled":false' "$BASE/result.json"
api POST /telemetry/retention/run '{"confirm":true}' 200
grep -q '"enabled":false' "$BASE/result.json"
api PUT /telemetry/retention '{"enabled":true,"rawDays":1,"hourlyDays":30,"confirm":true}' 400

api POST /telemetry/metrics "{\"deviceId\":\"$SNMP_DEVICE\",\"metricKey\":\"rssi_dbm\",\"oid\":\"malicious-command;echo\",\"scale\":0.1,\"offset\":0,\"enabled\":true,\"direction\":\"below\",\"warningThreshold\":-65,\"criticalThreshold\":-75,\"recoveryMargin\":3,\"confirm\":true}" 400
api POST /telemetry/metrics "{\"deviceId\":\"$SNMP_DEVICE\",\"metricKey\":\"rssi_dbm\",\"oid\":\".1.3.6.1.4.1.424242.1.0\",\"scale\":0.1,\"offset\":0,\"enabled\":true,\"direction\":\"below\",\"warningThreshold\":-65,\"criticalThreshold\":-75,\"recoveryMargin\":3,\"confirm\":true}" 201
RF_RSSI="$(id)"
api POST /telemetry/metrics "{\"deviceId\":\"$SNMP_DEVICE\",\"metricKey\":\"cpu_pct\",\"oid\":\".1.3.6.1.4.1.424242.2.0\",\"scale\":1,\"offset\":0,\"enabled\":true,\"direction\":\"above\",\"warningThreshold\":70,\"criticalThreshold\":90,\"recoveryMargin\":5,\"confirm\":true}" 201
RF_CPU="$(id)"
api GET /telemetry/metrics "" 200
python3 - "$BASE/result.json" "$RF_RSSI" "$RF_CPU" <<'PY'
import json,sys
metrics=json.load(open(sys.argv[1]))
assert {r['id'] for r in metrics}=={sys.argv[2],sys.argv[3]},metrics
assert 'secret_ciphertext' not in open(sys.argv[1]).read()
PY
api POST /telemetry/metrics "{\"deviceId\":\"$SNMP_DEVICE\",\"metricKey\":\"rssi_dbm\",\"oid\":\".1.3.6.1.4.1.424242.1.0\",\"scale\":0.1,\"offset\":0,\"enabled\":true,\"direction\":\"below\",\"warningThreshold\":-65,\"criticalThreshold\":-75,\"recoveryMargin\":3,\"confirm\":true}" 409

api PUT "/snmp/devices/$SNMP_DEVICE" '{"enabled":true,"intervalSeconds":300,"interfaceIndex":1,"confirm":true}' 200
for i in 1 2 3 4 5 6 7; do
 api POST "/snmp/devices/$SNMP_DEVICE/poll" '{}' 200
 grep -q '"polled":true' "$BASE/result.json"
done
api GET /telemetry/metrics "" 200
python3 - "$BASE/result.json" "$RF_RSSI" "$RF_CPU" <<'PY'
import json,sys
rows={x['id']:x for x in json.load(open(sys.argv[1]))}
assert rows[sys.argv[2]]['severity']=='normal',rows
assert float(rows[sys.argv[2]]['last_value'])==-60,rows
assert rows[sys.argv[3]]['severity']=='normal',rows
assert float(rows[sys.argv[3]]['last_value'])==30,rows
PY
api GET "/telemetry/metrics/$RF_RSSI/history?hours=24" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)==7,rows
assert float(rows[0]['raw_value'])==-550,rows
assert float(rows[0]['value'])==-55,rows
assert float(rows[-1]['value'])==-60,rows
PY
api GET /telemetry/events "" 200
python3 - "$BASE/result.json" "$RF_RSSI" "$RF_CPU" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
for ident in sys.argv[2:]:
 events=[r for r in rows if r['profile_id']==ident]
 states=[r['severity'] for r in reversed(events)]
 assert states==['warning','critical','normal'],states
 assert all(r['suppressed'] is False for r in events),events
PY
api GET /telemetry/sites "" 200
python3 - "$BASE/result.json" "$SITE" <<'PY'
import json,sys
site=next(x for x in json.load(open(sys.argv[1])) if x['id']==sys.argv[2])
assert int(site['metrics'])==2,site
assert int(site['warnings'])==0 and int(site['critical'])==0,site
PY

# Unsupported but syntactically valid vendor OID must not change IF-MIB state
# or create false custom measurements for otherwise healthy devices.
api POST /telemetry/metrics "{\"deviceId\":\"$SNMP_DEVICE\",\"metricKey\":\"snr_db\",\"oid\":\".1.3.6.1.4.1.424242.9.0\",\"scale\":1,\"offset\":0,\"enabled\":true,\"direction\":\"below\",\"warningThreshold\":null,\"criticalThreshold\":null,\"recoveryMargin\":0,\"confirm\":true}" 201
RF_BAD="$(id)"
api POST "/snmp/devices/$SNMP_DEVICE/poll" '{}' 200
grep -q '"state":"up"' "$BASE/result.json"
api GET "/telemetry/metrics/$RF_RSSI/history?hours=24" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
assert len(json.load(open(sys.argv[1])))==7
PY
api PUT "/telemetry/metrics/$RF_BAD" '{"enabled":false,"direction":"below","warningThreshold":null,"criticalThreshold":null,"recoveryMargin":0,"confirm":false}' 200
CODE="$(curl -sS -o "$BASE/result.json" -w '%{http_code}' \
 -H "Cookie: $NEW_PORTAL_COOKIE" "$BASEURL/api/telemetry/metrics")"
test "$CODE" = 401

# Insert two historical readings in the same complete hour for a deterministic
# hourly aggregate; use current synthetic values only in CI. These are not
# customer or physical radio measurements.
docker exec "$PGCONTAINER" psql -U netforge_ci -d netforge_ci -v ON_ERROR_STOP=1 -c "
  INSERT INTO snmp_metric_samples(profile_id,observed_at,raw_value,value)
  VALUES ('$RF_RSSI',date_trunc('hour',now()-interval '10 days')+interval '5 minutes',-650,-65),
         ('$RF_RSSI',date_trunc('hour',now()-interval '10 days')+interval '10 minutes',-700,-70);
  INSERT INTO snmp_samples(device_id,observed_at,uptime_ticks,oper_status,
     input_octets,output_octets,input_bps,output_bps,counter_reset)
  VALUES ('$SNMP_DEVICE',date_trunc('hour',now()-interval '10 days')+interval '5 minutes',
          1000,1,10000,20000,8000,16000,false);
" >/dev/null

api GET /telemetry/retention "" 200
grep -q '"enabled":false' "$BASE/result.json"
api PUT /telemetry/retention '{"enabled":true,"rawDays":7,"hourlyDays":30,"confirm":true}' 200
api POST /telemetry/retention/run '{"confirm":true}' 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
row=json.load(open(sys.argv[1]))
assert row['enabled'] is True,row
assert row['rfHours']>=1,row
assert row['interfaceHours']>=1,row
assert row['rfRawDeleted']>=2,row
assert row['interfaceRawDeleted']>=1,row
PY
api GET "/telemetry/metrics/$RF_RSSI/history?hours=720&resolution=hourly" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)>=1,rows
sample=rows[0]
assert int(sample['samples'])==2,sample
assert float(sample['minimum'])==-70 and float(sample['maximum'])==-65,sample
assert float(sample['average'])==-67.5,sample
PY
api GET "/telemetry/interface-hourly/$SNMP_DEVICE?hours=720" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)>=1,rows
assert int(rows[0]['samples'])>=1,rows
PY
api GET "/telemetry/metrics/$RF_RSSI/history?hours=720&resolution=raw" "" 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
assert len(rows)==7,rows
PY
api POST /telemetry/retention/run '{"confirm":true}' 200
python3 - "$BASE/result.json" <<'PY'
import json,sys
result=json.load(open(sys.argv[1]))
assert result['rfHours']==0 and result['interfaceHours']==0,result
assert result['rfRawDeleted']==0 and result['interfaceRawDeleted']==0,result
PY
api PUT /telemetry/retention '{"enabled":false,"rawDays":7,"hourlyDays":30,"confirm":true}' 200
api PUT "/snmp/devices/$SNMP_DEVICE" '{"enabled":false,"intervalSeconds":300,"interfaceIndex":1,"confirm":false}' 200
echo "RF/CPU vendor-OID mapping, threshold hysteresis and opt-in telemetry retention passed."
