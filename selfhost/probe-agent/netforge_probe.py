#!/usr/bin/env python3
# root/selfhost/probe-agent/netforge_probe.py
"""One-shot subscriber-side diagnostics. Python 3.11+, standard library only.

Run from an authorized subscriber LAN on a dedicated host; schedule each
invocation with systemd. No remote control, traffic capture, credentials
for customer devices, proxy discovery or automatic configuration downloads.
"""
import argparse
import datetime as dt
import json
import os
import queue
import re
import socket
import ssl
import stat
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

DEADLINE = 8
REPORT_DEADLINE = 12
MAX_CONFIG_BYTES = 8192

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # Refuse redirects, particularly for the bearer-authenticated upload.
        return None

def opener(context=None):
    return urllib.request.build_opener(
        urllib.request.ProxyHandler({}), NoRedirect(),
        urllib.request.HTTPSHandler(context=context or ssl.create_default_context())
    )

def dns_name(value):
    return (isinstance(value, str) and 3 <= len(value) <= 253
            and "." in value and all(
                1 <= len(part) <= 63
                and re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?", part)
                for part in value.split(".")))

def https_url(value, *, report=False):
    if not isinstance(value, str) or not 1 <= len(value) <= 512:
        return False
    try:
        parsed = urllib.parse.urlsplit(value)
        if (parsed.scheme != "https" or not parsed.hostname or
                parsed.username is not None or parsed.password is not None or
                parsed.query or parsed.fragment or
                any(ord(c) < 33 or ord(c) > 126 for c in value)):
            return False
        if report:
            # The report endpoint must be exact, with no redirect or query.
            return bool(re.fullmatch(
                r"/api/probe-report/[0-9a-fA-F-]{36}", parsed.path
            ) and 1 <= (parsed.port or 443) <= 65535)
        return dns_name(parsed.hostname) and (parsed.port or 443) == 443
    except ValueError:
        return False

def load_config(path):
    # Avoid following symlinks or reading a world-readable credential file.
    # The service account should own the file, with mode 0600 or 0400.
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0)
    with os.fdopen(os.open(path, flags), "rb") as fp:
        info = os.fstat(fp.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_mode & 0o077:
            raise ValueError("Configuration must be a regular file with permissions 0600 or 0400")
        data = fp.read(MAX_CONFIG_BYTES + 1)
    if len(data) > MAX_CONFIG_BYTES:
        raise ValueError("Configuration exceeds size limit")
    cfg = json.loads(data)
    if not isinstance(cfg, dict):
        raise ValueError("Configuration must be a JSON object")
    keys = {"reportUrl", "token", "dnsHost", "httpsUrl", "expectedStatus", "intervalSeconds"}
    if set(cfg) != keys:
        raise ValueError("Configuration must contain exactly the six exported fields")
    if not isinstance(cfg["token"], str) or not re.fullmatch(r"[0-9a-f]{64}", cfg["token"]):
        raise ValueError("Invalid enrollment token")
    if not dns_name(cfg["dnsHost"]) or not https_url(cfg["httpsUrl"]):
        raise ValueError("Invalid configured DNS/HTTPS target")
    if not https_url(cfg["reportUrl"], report=True):
        raise ValueError("Report URL must be an exact HTTPS NetForge probe endpoint")
    if type(cfg["expectedStatus"]) is not int or not 200 <= cfg["expectedStatus"] <= 299:
        raise ValueError("Expected status must be an integer in 200–299")
    if type(cfg["intervalSeconds"]) is not int or not 60 <= cfg["intervalSeconds"] <= 3600:
        raise ValueError("Expected cadence must be 60–3600 seconds")
    return cfg

def bounded(call, deadline):
    """Bound even platform DNS resolution (socket timeout does not bound libc).

    A daemon thread can outlive the timeout only until this one-shot process
    exits. Never use this function as an unbounded loop in a long-lived agent.
    """
    results = queue.Queue(maxsize=1)
    start = time.monotonic()
    def worker():
        try:
            results.put(call())
        except (TimeoutError, socket.timeout):
            results.put(("timeout", None))
        except Exception:
            # Do not write URLs, bearer tokens, response bodies or exception
            # text to logs. This is a diagnostic category, not a root cause.
            results.put(("failed", None))
    threading.Thread(target=worker, daemon=True).start()
    try:
        result, status = results.get(timeout=deadline)
    except queue.Empty:
        result, status = "timeout", None
    elapsed = min(30000, int((time.monotonic() - start) * 1000))
    return result, elapsed, status

def check_dns(host):
    # System resolver (including local caching and hosts files); no query
    # history, IP addresses or personal traffic is reported.
    addresses = socket.getaddrinfo(host, None, type=socket.SOCK_STREAM)
    return ("ok" if addresses else "failed"), None

def check_https(url, expected, context=None):
    # TLS certificate and hostname verification are mandatory. No response
    # body is consumed; only the response status is used as evidence.
    request = urllib.request.Request(
        url, headers={"User-Agent": "NetForge-Subscriber-Probe/1", "Accept": "*/*"},
        method="GET"
    )
    try:
        with opener(context).open(request, timeout=DEADLINE) as response:
            status = response.status
    except urllib.error.HTTPError as error:
        status = error.code
        error.close()
    except urllib.error.URLError as error:
        if isinstance(error.reason, (TimeoutError, socket.timeout)):
            return "timeout", None
        return "failed", None
    return ("ok" if status == expected else "failed"), status

def send(cfg, report, context=None):
    body = json.dumps(report, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    request = urllib.request.Request(
        cfg["reportUrl"], data=body, method="POST",
        headers={"Authorization": "Bearer " + cfg["token"],
                 "Content-Type": "application/json", "Accept": "application/json",
                 "User-Agent": "NetForge-Subscriber-Probe/1"}
    )
    try:
        with opener(context).open(request, timeout=REPORT_DEADLINE) as response:
            return ("ok" if response.status == 200 else "failed"), response.status
    except urllib.error.HTTPError as error:
        status = error.code
        error.close()
        return "failed", status

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", default="/etc/netforge-probe/agent.json")
    args = parser.parse_args()
    try:
        cfg = load_config(args.config)
    except (OSError, ValueError, TypeError, UnicodeError):
        print("NetForge probe: invalid or inaccessible configuration; check file and permissions",
              file=sys.stderr)
        return 2
    dns, dns_ms, _ = bounded(lambda: check_dns(cfg["dnsHost"]), DEADLINE)
    https, https_ms, status = bounded(
        lambda: check_https(cfg["httpsUrl"], cfg["expectedStatus"]), DEADLINE)
    report = {
        "measurementId": str(uuid.uuid4()),
        "measuredAt": dt.datetime.now(dt.timezone.utc).isoformat(),
        "dnsHost": cfg["dnsHost"], "httpsUrl": cfg["httpsUrl"],
        "dnsResult": dns, "dnsMs": dns_ms,
        "httpsResult": https, "httpsMs": https_ms, "httpsStatus": status,
    }
    upload, _, response_status = bounded(lambda: send(cfg, report), REPORT_DEADLINE)
    # Failed delivery is explicitly not backfilled as an observed outage.
    # Next invocation reports new observations; missing samples remain unknown.
    if upload != "ok":
        print("NetForge probe: report not accepted (HTTP status "
              + str(response_status or "unavailable") + ")", file=sys.stderr)
        return 1
    print("NetForge probe: report accepted; DNS=" + dns + " HTTPS=" + https)
    return 0

if __name__ == "__main__":
    sys.exit(main())
