# root/selfhost/tests/snmpget-mock.py
# Deterministic SNMPv3 IF-MIB samples for SQL/authorization/counter tests.
# This is NOT a wire-protocol emulator and does not prove SNMPv3 negotiation.
import os
from pathlib import Path
import stat
import sys

arguments = sys.argv[1:]
if "-A" in arguments or "-X" in arguments or "-c" in arguments:
    raise RuntimeError("SNMP credentials or community passed on command line")
if "-v" not in arguments or arguments[arguments.index("-v") + 1] != "3":
    raise RuntimeError("SNMPv3 not enforced")
if "-l" not in arguments or arguments[arguments.index("-l") + 1] != "authPriv":
    raise RuntimeError("SNMP authPriv not enforced")

folder = Path(os.environ["SNMPCONFPATH"])
conf = folder / "snmp.conf"
if not conf.exists():
    raise RuntimeError("SNMP configuration missing")
if stat.S_IMODE(folder.stat().st_mode) != 0o700:
    raise RuntimeError("SNMP configuration directory is not private")
if stat.S_IMODE(conf.stat().st_mode) != 0o600:
    raise RuntimeError("SNMP credential file is not private")
contents = conf.read_text()
for value in (
    "defVersion 3",
    "defSecurityLevel authPriv",
    "defAuthType SHA-256",
    "defPrivType AES",
    "defAuthPassphrase CI-AuthSecret-Strong-2026",
    "defPrivPassphrase CI-PrivacySecret-Strong-2026",
):
    if value not in contents:
        raise RuntimeError("SNMP configuration option missing")

# Custom vendor OID mock responses are a separate read, and must not
# alter the baseline IF-MIB counter stream or device-health state.
oids = [arg for arg in arguments if arg.startswith(".1.")]
if len(oids) != 4:
    rf_counter = Path(os.environ["NETFORGE_TEST_RF_COUNTER_FILE"])
    call = int(rf_counter.read_text()) + 1 if rf_counter.exists() else 1
    rf_counter.write_text(str(call))
    rssi = [-55, -70, -70, -80, -80, -60, -60]
    cpu = [20, 75, 75, 91, 95, 30, 30]
    values = {
        ".1.3.6.1.4.1.424242.1.0": rssi[min(call-1, 6)] * 10,
        ".1.3.6.1.4.1.424242.2.0": cpu[min(call-1, 6)],
    }
    if any(oid not in values for oid in oids):
        sys.exit(1)
    for oid in oids:
        print(values[oid], flush=True)
    sys.exit(0)

counter_path = Path(os.environ["NETFORGE_TEST_SNMP_COUNTER_FILE"])
number = int(counter_path.read_text()) + 1 if counter_path.exists() else 1
counter_path.write_text(str(number))
samples = [
    (100000, 1, 1000000, 2000000),
    (101000, 1, 2000000, 3000000),
    (100, 1, 1000, 2000),
    (110, 2, 2000, 3000),
    (120, 2, 3000, 4000),
    (130, 2, 4000, 5000),
    (140, 1, 5000, 6000),
]
sample = samples[min(number - 1, len(samples) - 1)]
for value in sample:
    print(value, flush=True)
