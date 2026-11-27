# root/selfhost/tests/routeros-rest-mock.py
"""Disposable HTTPS fixture: reject unexpected credentials, verbs and paths."""
import base64
import http.server
import json
import ssl
import sys
import urllib.parse

EXPECTED = "Basic " + base64.b64encode(b"ci-reader:ci-router-password-2026").decode()
PATHS = {"system/resource", "system/identity", "system/routerboard", "interface",
         "interface/vlan", "interface/bridge", "ip/address", "ip/route",
         "interface/pppoe-server/server", "ip/dhcp-server"}

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        url = urllib.parse.urlsplit(self.path)
        path = url.path.removeprefix("/rest/")
        if self.headers.get("Authorization") != EXPECTED:
            self.send_error(401)
            return
        if not url.path.startswith("/rest/") or path not in PATHS or not urllib.parse.parse_qs(url.query).get(".proplist"):
            self.send_error(400)
            return
        # Extra secret must be dropped even if the router ignores .proplist.
        data = [{"version":"7.19.3","board-name":"CI-CCR","password":"NEVER_STORE_THIS"}] if path == "system/resource" else [{"name":"test","password":"NEVER_STORE_THIS"}]
        body = json.dumps(data).encode()
        self.send_response(200)
        self.send_header("Content-Type","application/json")
        self.send_header("Content-Length",str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        # Never echo Authorization headers or body.
        pass

server = http.server.ThreadingHTTPServer((sys.argv[1], int(sys.argv[2])), Handler)
tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
tls.load_cert_chain(sys.argv[3], sys.argv[4])
server.socket = tls.wrap_socket(server.socket, server_side=True)
server.serve_forever()
