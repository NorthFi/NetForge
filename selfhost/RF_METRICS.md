# root/selfhost/RF_METRICS.md
# NetForge — RF metrics, threshold alerting and long-term telemetry (Phase 7)

Phase 7 extends the independent .NET 11/PostgreSQL NetForge ISP stack with
read-only, administrator-defined SNMPv3 measurement OIDs. It does **not**
include the removed RF link planner, and it does not copy any proprietary
vendor interface or ship undocumented OID mappings.

## 1. Supported measurements

Each registered SNMPv3 device can be associated with up to 12 enabled or
disabled scalar mappings, **one mapping per metric key and device**:

| Metric key | Meaning | Unit |
| --- | --- | --- |
| `rssi_dbm`, `rssi_chain0_dbm`, `rssi_chain1_dbm` | Received RF power | dBm |
| `snr_db` | Signal-to-noise ratio | dB |
| `noise_dbm` | Noise floor | dBm |
| `ccq_pct`, `tx_ccq_pct` | Link quality, when exposed by device vendor | % |
| `rx_capacity_mbps`, `tx_capacity_mbps` | Vendor-reported capacity estimates | Mbps |
| `cpu_pct`, `memory_pct` | Device utilization | % |
| `voltage_v`, `power_w` | Supply voltage and power | V, W |
| `temperature_c` | Device temperature | °C |
| `frequency_mhz` | Operating frequency | MHz |

**The units describe the normalized value NetForge stores. They are NOT
guaranteed by an arbitrary OID.** An administrator must verify the
device/firmware MIB, instance suffix, sign conventions, and scale for the
exact hardware. For example, a radio may report tenths of dBm, in which
case enter `scale=0.1`; if its vendor uses a different encoding, derive
the appropriate multiplier and offset from the vendor specification.

The value is computed as `raw SNMP scalar × scale + offset`. Values are
stored in `snmp_metric_samples` with UTC observation timestamps. Only
plain integer or decimal scalar responses are accepted. Hex strings, SNMP
OCTET STRING values, enumerated labels, missing instances and unexpected
output are skipped. Unsupported custom OIDs do **not** mark the standard
SNMP IF-MIB device health as DOWN. A failure of the optional OID batch
means no vendor-specific sample is written for that attempt.

Numeric OIDs are bounded and validated. OIDs are queried through the
existing SNMPv3 authPriv session and never execute scripts or vendor device
commands. NetForge does not allow public client access to these mappings.

## 2. Onboarding a MikroTik, Cambium, Ubiquiti or Mimosa radio

1. Register the device in NetForge inventory with its correct management
   IPv4 address, then create an **SNMPv3 authPriv** profile for it.
2. Enable read-only SNMPv3 on the radio, restrict UDP 161 ingress to the
   API server/management VPN source, and verify its privacy/authentication
   support for the firmware release.
3. Obtain a **documented, numeric SNMP OID** from the exact device's MIB,
   including any correct radio-chain/interface index.
4. Independently read that OID with a known-good Net-SNMP client using
   credential files rather than command-line secrets. Compare the result
   to the vendor GUI or radio diagnostics under stable conditions.
5. In **RF Metrics**, choose the SNMPv3 profile and metric key; provide
   the fully qualified numeric OID, raw-value multiplier, offset, and
   direction. Leave alert thresholds empty until the scale is confirmed.
6. After verifying multiple samples and scale, optionally enter warning
   and critical thresholds, recovery margin, and enable alerting.
7. Verify historical samples and a controlled recovery cycle before
   enabling production operational notifications.

**No built-in vendor OID mappings are assumed in this release.** RouterOS,
AirMAX/Wave, ePMP/PTP, Mimosa C-series and other radio platforms vary in
firmware, MIB availability and measurement definitions. Avoid reusing
an OID from a different radio family without a documented match.

## 3. Alert threshold state machine

Thresholds operate on the **scaled** numeric value:

- `below`: warning when value is at or below warning; critical at or
  below critical. Critical must be no greater than warning.
- `above`: warning when value is at or above warning; critical at or
  above critical. Critical must be no less than warning.
- Both warning and critical must be set together, or both omitted
  (sample collection without alerting).
- **Debounce:** two consecutive valid samples must indicate the same
  new severity before a transition is recorded. The initial profile
  state is `normal`. No alert is sent on an invalid/missing sample.
- **Hysteresis:** when recovering from a bad state, the metric must
  cross the appropriate boundary by the configured recovery margin
  before transitioning. Recovery also requires two valid consecutive
  samples. This prevents flapping close to the threshold.
- **Notifications:** a severity change is persisted in
  `snmp_metric_events` and may enqueue an operations email or Telegram
  notification if those channels have been explicitly enabled.
- **Highsite dependency:** alerts are marked `suppressed` while a tagged
  upstream TCP monitor for that site is DOWN. The individual metric
  event and samples are still preserved in the database.

Changing threshold policy resets the *active severity* to normal, but
does not delete historical events or measurements. Operators should
document why the policy was changed. A sudden RSSI decrease can be caused
by a link path issue, weather, power or configuration; the metric alone
does not establish root cause.

## 4. Historical charts and storage

The **RF Metrics** screen provides per-metric time-series charts for
recent raw readings or hourly rollups. Each hour stores minimum,
maximum, average, and the number of samples. The SNMP Telemetry view
continues to store the IF-MIB interface counters and bitrate; a separate
`snmp_interface_hourly` table aggregates average and peak input/output
bitrate and the number of operational samples.

The **Telemetry Retention** screen controls a global policy:

| Setting | Default | Allowed |
| --- | --- | --- |
| Automatic retention | **Disabled** | Explicit admin confirmation |
| Raw sample retention | 90 days | 7–365 days |
| Hourly rollup retention | 365 days | 30–1825 days; not shorter than raw |
| Maintenance frequency | Every 6 hours | Fixed per server worker |
| Max raw records deleted per execution | 200,000 per table | Bounded safeguard |
| Max older hourly records deleted per execution | 10,000 per table | Bounded safeguard |

Maintenance rolls up **completed hours first** in a PostgreSQL
transaction, then deletes expired raw records only where a matching
hourly rollup exists. The entire transaction either commits or rolls
back; no partial successful deletion is reported. An independent
advisory lock prevents simultaneous maintenance from multiple API
replicas. The worker only runs maintenance when the operator has
explicitly enabled it. Administrators can perform a confirmed manual
run and see counts of aggregated/pruned records.

**Retention is destructive.** After raw samples are removed, the hourly
aggregate cannot reconstruct every individual reading. An expired
hourly aggregate can only be restored from database backup. The
application does not offer an undo control.

### Scaling and storage considerations

This is a PostgreSQL native-table implementation; it does not yet use
TimescaleDB, native time partitioning, or pre-aggregated per-site RF
metrics. For thousands of devices, plan write throughput, index size,
maintenance windows, partitioning and aggregate retention before enabling
dense polling. A bounded deletion backlog may require repeated runs.
Never enable deletion until backup and restore have been tested.

## 5. APIs and authorization

| Endpoint | Scope |
| --- | --- |
| `GET /api/telemetry/metrics` | Admin/dispatcher: list mappings without SNMP secrets |
| `POST /api/telemetry/metrics` | Admin: create numeric OID profile |
| `PUT /api/telemetry/metrics/{id}` | Admin: enable/disable and threshold policy |
| `GET /api/telemetry/metrics/{id}/history?hours=168&resolution=raw` | Staff: recent numeric samples |
| `GET /api/telemetry/metrics/{id}/history?hours=2160&resolution=hourly` | Staff: long-term aggregate |
| `GET /api/telemetry/events` | Staff: severity history and suppressed events |
| `GET /api/telemetry/sites` | Staff: site-level warning/critical totals |
| `GET /api/telemetry/interface-hourly/{deviceId}?hours=2160` | Staff: IF-MIB hourly bitrate averages and peaks |
| `GET/PUT /api/telemetry/retention` | Admin: explicit cleanup policy |
| `POST /api/telemetry/retention/run` | Admin: confirmed maintenance |

Customer portal sessions cannot access staff telemetry endpoints.
CSRF protection applies to all staff mutations.

## 6. Production acceptance

The CI tests demonstrate numeric OID input validation, scaling,
debounced warning/critical/recovery sequences, raw and hourly samples,
bad-OID isolation, highsite integration, at-rest SNMP key protection,
and protected retention. The existing isolated SNMPv3 SHA-256/AES wire
test and FreeRADIUS tests continue to run as regression gates.

**CI does not validate actual vendor OID definitions**. Accept each
MikroTik/Cambium/Ubiquiti/Mimosa device model and firmware manually
using real hardware and compare displayed values with vendor tools.
Confirm OID semantics, unit normalization, alert thresholds, network
egress restrictions, Data Protection key backup and PostgreSQL restore
before production use.

Migration sequence is additive:
`007_rf_telemetry_thresholds.sql` creates custom metric profiles,
events, samples and generic hourly bins; `008_snmp_interface_hourly.sql`
adds long-term IF-MIB summaries. Existing billing and RADIUS schema
and existing SNMP historical samples remain intact.
