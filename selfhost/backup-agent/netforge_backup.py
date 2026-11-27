# root/selfhost/backup-agent/netforge_backup.py
"""Linux collector: fixed read-only RouterOS SSH export or existing vendor file.
Uses standard Python and OpenSSH, no arbitrary command supplied by NetForge.
"""
import argparse
import ipaddress
import json
import os
import pathlib
import re
import selectors
import ssl
import stat
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

MAX_BYTES = 4 * 1024 * 1024

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

def read_file(path):
    with open(path, "rb") as source:
        if not stat.S_ISREG(os.fstat(source.fileno()).st_mode):
            raise ValueError("backup source must be a regular file")
        data = source.read(MAX_BYTES + 1)
    if not 0 < len(data) <= MAX_BYTES:
        raise ValueError("backup outside size limit")
    return data

def export_router(config):
    # Literal IP prevents shell expansion and DNS redirection. Dedicated key,
    # pinned host key and fixed command; never use password auth or ssh-agent.
    host = str(ipaddress.IPv4Address(config["router_ip"]))
    user = config["router_user"]
    port = int(config.get("router_port", 22))
    if not re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", user) or not 1 <= port <= 65535:
        raise ValueError("invalid router user or port")
    key = pathlib.Path(config["identity_file"]).resolve(strict=True)
    known = pathlib.Path(config["known_hosts"]).resolve(strict=True)
    if key.stat().st_mode & 0o077:
        raise ValueError("SSH key must not be accessible to group/others")
    args = ["/usr/bin/ssh", "-F", "/dev/null", "-T", "-n", "-p", str(port),
            "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
            "-o", "IdentityAgent=none", "-o", "StrictHostKeyChecking=yes",
            "-o", "UserKnownHostsFile=" + str(known), "-o", "GlobalKnownHostsFile=/dev/null",
            "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=10",
            "-o", "ServerAliveCountMax=2", "-o", "ClearAllForwardings=yes",
            "-o", "PermitLocalCommand=no", "-i", str(key), user + "@" + host,
            "/export compact"]
    deadline = time.monotonic() + 90
    data = bytearray()
    with subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as proc:
        try:
            with selectors.DefaultSelector() as sel:
                sel.register(proc.stdout, selectors.EVENT_READ)
                while True:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0 or not sel.select(remaining):
                        raise TimeoutError("RouterOS export timed out")
                    chunk = os.read(proc.stdout.fileno(), 8192)
                    if not chunk:
                        break
                    data.extend(chunk)
                    if len(data) > MAX_BYTES:
                        raise ValueError("RouterOS export exceeds limit")
            if proc.wait(timeout=max(0.1, deadline-time.monotonic())) != 0:
                raise RuntimeError("RouterOS SSH export failed")
        finally:
            if proc.poll() is None:
                proc.kill()
                proc.wait()
    text = data.decode("utf-8", errors="strict")
    # Reject known incomplete-export markers and obvious non-config responses.
    if not text.strip() or "#error exporting" in text.lower() or not any(
            line.startswith("/") for line in text.splitlines()):
        raise ValueError("RouterOS export appears incomplete")
    return bytes(data)

def upload(config, filename, kind, data):
    base = urllib.parse.urlsplit(config["server"])
    if (base.scheme != "https" or not base.hostname or base.username or
            base.password or base.query or base.fragment or base.path not in ("", "/")):
        raise ValueError("server must be a bare HTTPS origin")
    profile = str(uuid.UUID(config["profile_id"]))
    token = config["token"]
    if not re.fullmatch(r"[0-9A-Fa-f]{64}", token):
        raise ValueError("invalid upload token")
    if not re.fullmatch(r"[A-Za-z0-9_-][A-Za-z0-9_.-]{0,159}", filename):
        raise ValueError("invalid filename")
    if kind not in ("text", "binary"):
        raise ValueError("invalid backup kind")
    query = urllib.parse.urlencode({"filename": filename, "kind": kind})
    url = urllib.parse.urlunsplit((base.scheme, base.netloc,
                                   "/api/backup-ingest/" + profile, query, ""))
    context = ssl.create_default_context(cafile=config.get("ca_file"))
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                        urllib.request.HTTPSHandler(context=context), NoRedirect())
    request = urllib.request.Request(url, data=data, method="POST", headers={
        "Authorization": "Bearer " + token, "Content-Type": "application/octet-stream"})
    with opener.open(request, timeout=30) as response:
        if response.status not in (200, 201):
            raise RuntimeError("unexpected upload response")
        result = json.loads(response.read(4096))
        if not isinstance(result.get("duplicate"), bool):
            raise RuntimeError("invalid upload acknowledgement")
        print("Backup accepted; duplicate=" + str(result["duplicate"]).lower())

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=pathlib.Path)
    args = parser.parse_args()
    try:
        with args.config.open("r", encoding="utf-8") as source:
            if os.fstat(source.fileno()).st_mode & 0o077:
                raise ValueError("collector configuration requires private permissions")
            config = json.load(source)
        if config["mode"] == "routeros-ssh":
            data = export_router(config)
            filename, kind = "routeros-export.rsc", "text"
        elif config["mode"] == "file":
            source = pathlib.Path(config["file"])
            data = read_file(source)
            filename, kind = source.name, config["kind"]
        else:
            raise ValueError("unknown collector mode")
        upload(config, filename, kind, data)
        return 0
    except urllib.error.HTTPError as exc:
        print("Backup upload failed: HTTP " + str(exc.code), file=sys.stderr)
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, subprocess.SubprocessError):
        # Do not echo exception messages, file contents, credentials or URLs.
        print("Backup collection failed; check local configuration and connectivity", file=sys.stderr)
    return 1

if __name__ == "__main__":
    sys.exit(main())
