# root/selfhost/execution-agent/netforge_execute.py
"""One-shot, device-bound approved RouterOS execution. Never retries a command."""
import argparse
import datetime
import hashlib
import ipaddress
import json
import os
import pathlib
import re
import selectors
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

MAX_OUTPUT = 65536

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None

def load_config(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, "r", encoding="utf-8") as stream:
        import stat
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_mode & 0o077 or info.st_size > 65536:
            raise ValueError("private regular configuration required")
        cfg = json.load(stream)
    origin = urllib.parse.urlsplit(cfg["server"])
    if (origin.scheme != "https" or not origin.hostname or origin.username or
            origin.password or origin.path not in ("", "/") or origin.query or origin.fragment):
        raise ValueError("HTTPS origin required")
    cfg["server"] = urllib.parse.urlunsplit((origin.scheme, origin.netloc, "", "", ""))
    cfg["profile_id"] = str(uuid.UUID(cfg["profile_id"]))
    cfg["router_ip"] = str(ipaddress.IPv4Address(cfg["router_ip"]))
    if not re.fullmatch(r"[A-Fa-f0-9]{64}", cfg["token"]):
        raise ValueError("invalid token")
    hashes = cfg["allowed_sha256"]
    if not isinstance(hashes, list) or not hashes or not all(
            isinstance(x, str) and re.fullmatch(r"[A-Fa-f0-9]{64}", x) for x in hashes):
        raise ValueError("local command hash allowlist required")
    cfg["allowed_sha256"] = {x.upper() for x in hashes}
    return cfg

def request(cfg, path, data):
    context = ssl.create_default_context(cafile=cfg.get("ca_file"))
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                         urllib.request.HTTPSHandler(context=context), NoRedirect())
    req = urllib.request.Request(cfg["server"] + "/api/execution-agent/" + cfg["profile_id"] + path,
                                 data=json.dumps(data).encode(), method="POST",
                                 headers={"Content-Type":"application/json",
                                          "Authorization":"Bearer "+cfg["token"]})
    with opener.open(req, timeout=15) as response:
        if response.status == 204:
            return None
        raw = response.read(65537)
        if len(raw) > 65536:
            raise ValueError("oversize response")
        return json.loads(raw)

def approved(cfg, job):
    cmd = job["command"]
    if (job["profileId"] != cfg["profile_id"] or job["address"] != cfg["router_ip"] or
            not isinstance(cmd, str) or not 0 < len(cmd) <= 4096 or
            cmd[0] not in "/:" or not all(32 <= ord(c) <= 126 for c in cmd)):
        return False
    digest = hashlib.sha256(cmd.encode("ascii")).hexdigest().upper()
    return digest == job["sha256"] and digest in cfg["allowed_sha256"]

def ssh_args(cfg, cmd):
    user = cfg["router_user"]
    port = int(cfg.get("router_port", 22))
    if not re.fullmatch(r"[A-Za-z0-9_.-]{1,80}", user) or not 1 <= port <= 65535:
        raise ValueError("invalid router SSH identity")
    key = pathlib.Path(cfg["identity_file"]).resolve(strict=True)
    known = pathlib.Path(cfg["known_hosts"]).resolve(strict=True)
    if key.stat().st_mode & 0o077 or not key.is_file() or not known.is_file():
        raise ValueError("private SSH key and known_hosts required")
    return ["/usr/bin/ssh", "-F", "/dev/null", "-T", "-n", "-p", str(port),
            "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none",
            "-o", "StrictHostKeyChecking=yes", "-o", "UserKnownHostsFile="+str(known),
            "-o", "GlobalKnownHostsFile=/dev/null", "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=2",
            "-o", "ClearAllForwardings=yes", "-o", "PermitLocalCommand=no",
            "-i", str(key), user+"@"+cfg["router_ip"], cmd]

def execute(cfg, job):
    if not approved(cfg, job):
        return "refused", 0, None
    deadline = datetime.datetime.fromisoformat(job["deadline"].replace("Z", "+00:00"))
    remaining = (deadline-datetime.datetime.now(datetime.timezone.utc)).total_seconds()
    if remaining < 65:
        return "refused", 0, None
    args = ssh_args(cfg, job["command"])
    stop = time.monotonic()+60
    count = 0
    # No shell, no forwarding, no interactive input. Do not store raw output
    # or print it to logs; it may contain secrets. Exit status is not proof
    # of RouterOS configuration success. A timed-out command may continue remotely.
    try:
        with subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT) as proc:
            try:
                with selectors.DefaultSelector() as sel:
                    sel.register(proc.stdout, selectors.EVENT_READ)
                    while True:
                        left = stop-time.monotonic()
                        if left <= 0 or not sel.select(left):
                            return "timeout", count, None
                        chunk = os.read(proc.stdout.fileno(), 8192)
                        if not chunk:
                            break
                        count += len(chunk)
                        if count > MAX_OUTPUT:
                            return "output_limit", MAX_OUTPUT, None
                code = proc.wait(timeout=max(0.1,stop-time.monotonic()))
                return ("exit_zero" if code==0 else "nonzero"),count,code
            finally:
                if proc.poll() is None:
                    proc.kill()
                    proc.wait()
    except subprocess.TimeoutExpired:
        return "timeout", count, None
    except OSError:
        return "transport_error", count, None

def run(cfg):
    job = request(cfg,"/claim",{"address":cfg["router_ip"]})
    if job is None:
        print("No queued execution.")
        return 0
    target = str(uuid.UUID(job["targetId"]))
    claim = job["claim"]
    if not re.fullmatch(r"[A-Fa-f0-9]{64}", claim):
        raise ValueError("invalid claim")
    try:
        result,count,code = execute(cfg,job)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        result,count,code = "refused",0,None
    # Failure to report does not trigger re-execution. The server retains an
    # outstanding claim until an independent administrator resolves it.
    request(cfg,"/jobs/"+target+"/report",{"claim":claim,"result":result,
                                           "outputBytes":count,"exitCode":code})
    print("Execution transport result: "+result+"; operator verification required.")
    return 0 if result=="exit_zero" else 1

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config",type=pathlib.Path)
    args=parser.parse_args()
    try:
        return run(load_config(args.config))
    except (OSError, ValueError, KeyError, TypeError, RuntimeError):
        print("Agent request failed; inspect NetForge job state before any further action.",file=sys.stderr)
        return 1

if __name__=="__main__":
    sys.exit(main())
