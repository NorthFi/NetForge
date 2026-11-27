# root/selfhost/tests/test_backup_agent.py
"""Offline collector tests: safe file reading and real HTTPS upload validation."""
import http.server
import importlib.util
import json
import os
import pathlib
import ssl
import subprocess
import tempfile
import threading
import unittest
import urllib.error
import uuid

MODULE = pathlib.Path(__file__).resolve().parents[1] / "backup-agent" / "netforge_backup.py"
SPEC = importlib.util.spec_from_file_location("netforge_backup", MODULE)
agent = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(agent)

class Handler(http.server.BaseHTTPRequestHandler):
    calls = []
    redirect = False
    def do_POST(self):
        data = self.rfile.read(int(self.headers["Content-Length"]))
        self.calls.append((self.path, self.headers.get("Authorization"), data))
        if self.redirect:
            self.send_response(307)
            self.send_header("Location", "/unexpected")
            self.end_headers()
            return
        body = b'{"duplicate":false}'
        self.send_response(201)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args):
        pass

class BackupAgentTests(unittest.TestCase):
    def test_file_limits_and_types(self):
        with tempfile.TemporaryDirectory() as tmp:
            p = pathlib.Path(tmp) / "backup.rsc"
            p.write_bytes(b"/system identity\n")
            self.assertEqual(agent.read_file(p), p.read_bytes())
            link = pathlib.Path(tmp) / "link"
            link.symlink_to(p)
            with self.assertRaises(OSError):
                agent.read_file(link)
            fifo = pathlib.Path(tmp) / "fifo"
            os.mkfifo(fifo)
            with self.assertRaises(ValueError):
                agent.read_file(fifo)
            p.write_bytes(b"")
            with self.assertRaises(ValueError):
                agent.read_file(p)
            with p.open("wb") as f:
                f.truncate(agent.MAX_BYTES + 1)
            with self.assertRaises(ValueError):
                agent.read_file(p)

    def test_https_and_no_redirect(self):
        with tempfile.TemporaryDirectory() as tmp:
            cert, key = pathlib.Path(tmp) / "cert.pem", pathlib.Path(tmp) / "key.pem"
            subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048",
                            "-nodes", "-days", "1", "-subj", "/CN=localhost",
                            "-addext", "subjectAltName=IP:127.0.0.1",
                            "-keyout", str(key), "-out", str(cert)],
                           check=True, capture_output=True)
            server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(cert, key)
            server.socket = context.wrap_socket(server.socket, server_side=True)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            Handler.calls = []
            Handler.redirect = False
            profile = str(uuid.uuid4())
            config = {"server":f"https://127.0.0.1:{server.server_port}",
                      "profile_id":profile,"token":"A"*64,"ca_file":str(cert)}
            try:
                agent.upload(config, "backup.rsc", "text", b"test")
                self.assertEqual(Handler.calls, [
                    ("/api/backup-ingest/"+profile+"?filename=backup.rsc&kind=text",
                     "Bearer "+"A"*64, b"test")])
                with self.assertRaises(urllib.error.URLError):
                    agent.upload({k:v for k,v in config.items() if k!="ca_file"},
                                 "backup.rsc", "text", b"test")
                Handler.redirect = True
                with self.assertRaises(urllib.error.HTTPError) as error:
                    agent.upload(config, "backup.rsc", "text", b"test")
                self.assertEqual(error.exception.code, 307)
                self.assertEqual(len(Handler.calls), 2)
                with self.assertRaises(ValueError):
                    agent.upload({**config,"server":"http://127.0.0.1"},
                                 "backup.rsc","text",b"test")
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=5)

if __name__ == "__main__":
    unittest.main()
