#!/usr/bin/env python3
# root/selfhost/tests/radius-mock.py
"""One-shot RFC 5176 mock NAS; validates request and responds with an ACK or forged ACK."""
import argparse
import hashlib
import os
import socket
import struct

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, required=True)
parser.add_argument("--mode", choices=["ack", "invalid"], required=True)
args = parser.parse_args()
secret = os.environ["RADIUS_MOCK_SECRET"].encode()
with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
    sock.bind(("127.0.0.1", args.port))
    sock.settimeout(8.0)
    request, source = sock.recvfrom(4096)
    if len(request) < 20 or request[0] != 40:
        raise ValueError("Expected Disconnect-Request")
    if struct.unpack("!H", request[2:4])[0] != len(request):
        raise ValueError("Bad request length")
    authenticator = hashlib.md5(request[:4] + bytes(16) + request[20:] + secret).digest()
    if authenticator != request[4:20]:
        raise ValueError("Invalid request authenticator")
    attributes = {}
    pos = 20
    while pos < len(request):
        kind, size = request[pos], request[pos + 1]
        if size < 2 or pos + size > len(request):
            raise ValueError("Invalid attribute")
        attributes[kind] = request[pos + 2:pos + size]
        pos += size
    if attributes.get(1) != b"ci-pppoe@example.test" or not attributes.get(44):
        raise ValueError("Unexpected subscriber/session")
    if attributes.get(4) != socket.inet_aton("127.0.0.1"):
        raise ValueError("Unexpected NAS address")
    header = bytes([41, request[1], 0, 20])
    digest = hashlib.md5(header + request[4:20] + secret).digest()
    if args.mode == "invalid":
        digest = bytes(16)
    sock.sendto(header + digest, source)
    print(args.mode, flush=True)
