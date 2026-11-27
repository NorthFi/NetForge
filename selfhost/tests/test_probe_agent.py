# root/selfhost/tests/test_probe_agent.py
"""Local TLS wire tests; no external endpoints, no production credentials."""
import http.server
import importlib.util
import json
import os
from pathlib import Path
import ssl
import subprocess
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("netforge_probe",
    Path(__file__).resolve().parents[1] / "probe-agent" / "netforge_probe.py")
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/redirect":
            self.send_response(302)
            self.send_header("Location", "/never")
        elif self.path == "/ok":
            self.send_response(204)
        else:
            self.server.unexpected += 1
            self.send_response(500)
        self.end_headers()

    def do_POST(self):
        body = self.rfile.read(int(self.headers["Content-Length"]))
        self.server.uploads.append((self.path, self.headers.get("Authorization"),
                                    json.loads(body)))
        if self.path == "/redirect":
            self.send_response(307)
            self.send_header("Location", "/never")
        else:
            self.send_response(200)
        self.end_headers()

class ProbeAgentTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        d = Path(cls.temp.name)
        key, cert = d / "key.pem", d / "cert.pem"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048",
                        "-nodes", "-days", "1", "-subj", "/CN=localhost",
                        "-addext", "subjectAltName=DNS:localhost",
                        "-keyout", str(key), "-out", str(cert)],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        cls.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        cls.server.uploads = []
        cls.server.unexpected = 0
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.load_cert_chain(cert, key)
        cls.server.socket = tls.wrap_socket(cls.server.socket, server_side=True)
        cls.context = ssl.create_default_context(cafile=str(cert))
        cls.origin = "https://localhost:" + str(cls.server.server_port)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.temp.cleanup()

    def test_dns_and_deadline(self):
        self.assertEqual(agent.check_dns("localhost"), ("ok", None))
        result, elapsed, status = agent.bounded(lambda: time.sleep(1), 0.05)
        self.assertEqual((result, status), ("timeout", None))
        self.assertLess(elapsed, 700)

    def test_tls_status_and_redirect(self):
        self.assertEqual(agent.check_https(self.origin+"/ok",204,self.context),("ok",204))
        self.assertEqual(agent.check_https(self.origin+"/ok",200,self.context),("failed",204))
        self.assertEqual(agent.check_https(self.origin+"/redirect",204,self.context),("failed",302))
        self.assertEqual(self.server.unexpected,0)
        # Self-signed fixture is not trusted by the system trust store.
        self.assertEqual(agent.check_https(self.origin+"/ok",204),("failed",None))

    def test_reporting_redirect_not_followed(self):
        token = "a"*64
        payload = {"measurementId":"synthetic-test"}
        before = len(self.server.uploads)
        self.assertEqual(agent.send({"reportUrl":self.origin+"/report","token":token},
                                    payload,self.context),("ok",200))
        self.assertEqual(agent.send({"reportUrl":self.origin+"/redirect","token":token},
                                    payload,self.context),("failed",307))
        rows = self.server.uploads[before:]
        self.assertEqual([r[0] for r in rows], ["/report","/redirect"])
        self.assertTrue(all(r[1]=="Bearer "+token and r[2]==payload for r in rows))

    def test_config_permissions_and_validation(self):
        path = Path(self.temp.name)/"agent.json"
        cfg = {"reportUrl":"https://netforge.example.test/api/probe-report/12345678-1234-1234-1234-123456789012",
               "token":"b"*64,"dnsHost":"dns.example.test",
               "httpsUrl":"https://health.example.test/check","expectedStatus":204,
               "intervalSeconds":300}
        path.write_text(json.dumps(cfg))
        os.chmod(path,0o644)
        with self.assertRaises(ValueError):
            agent.load_config(path)
        os.chmod(path,0o600)
        self.assertEqual(agent.load_config(path),cfg)
        cfg["httpsUrl"]="https://username:secret@health.example.test/check"
        path.write_text(json.dumps(cfg))
        with self.assertRaises(ValueError):
            agent.load_config(path)
        self.assertFalse(agent.https_url("http://health.example.test"))
        self.assertFalse(agent.https_url("https://health.example.test/?q=secret"))

if __name__ == "__main__":
    unittest.main()
