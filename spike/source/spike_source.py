#!/usr/bin/env python3
"""DataspaceDrive Spike - Testquelle hinter der EDC-Dataplane (Anbieterseite).

Simuliert den spaeteren Content-Endpunkt der DataspaceDrive:

  Port 8090 (nur fuer die Dataplane-Pods, per NetworkPolicy abgesichert)
    GET /edc/deliveries/{deliveryId}[/items/{itemId}][?offset=&length=]
      - prueft das HMAC-Zustell-Token im Header X-Drive-Delivery-Token
      - streamt deterministische Testdaten sofort (die EDC-Dataplane bricht nach 30 s Leerlauf ab)
      - protokolliert alle eingehenden Header (Edc-Bpn, Edc-Contract-Agreement-Id, ...)
    GET /healthz

  Port 8091 (nur fuer den Spike-Runner)
    GET  /_spike/requests?since=N   Protokoll der Abrufe als JSON
    POST /_spike/reset              Protokoll leeren
    GET  /healthz

Nur Standardbibliothek, damit das offizielle python-Image ohne pip reicht.
"""
import base64
import hashlib
import hmac
import json
import os
import posixpath
import re
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MIB = 1024 * 1024
DELIVERY_KEY = os.environ["DELIVERY_KEY"].encode()
TOKEN_HEADER = "X-Drive-Delivery-Token"

# Groessen und Verhalten je Testfall; der Fall steht als letztes Segment in der Zustell-ID.
CASES = {
    "smalla": {"size": 1 * MIB},
    "smallb": {"size": 1 * MIB},
    "neg": {"size": 1 * MIB},
    "large": {"size": int(os.environ.get("LARGE_MB", "1024")) * MIB},
    "par": {"size": int(os.environ.get("PAR_MB", "128")) * MIB},
    "seg": {"size": 32 * MIB},
    "slow": {"size": 32 * MIB, "rate": 256 * 1024},
}

PATH_RE = re.compile(r"^/edc/deliveries/([^/]+)(?:/items/([^/]+))?/?$")
CHUNK = 64 * 1024


def _pattern_block() -> bytes:
    """1 MiB deterministischer Inhalt; der Runner erzeugt denselben Block fuer den Soll-Hash."""
    seed = b"dataspacedrive-spike"
    return b"".join(hashlib.sha256(seed + i.to_bytes(8, "big")).digest() for i in range(MIB // 32))


BLOCK = _pattern_block()


def content_slice(start: int, end: int):
    """Liefert die Bytes [start, end) des endlos wiederholten Musterblocks in Stuecken."""
    pos = start
    while pos < end:
        off = pos % MIB
        take = min(end - pos, MIB - off, CHUNK)
        yield BLOCK[off:off + take]
        pos += take


def expected_token(delivery_id: str) -> str:
    mac = hmac.new(DELIVERY_KEY, delivery_id.encode(), hashlib.sha256).digest()
    return "v1." + base64.urlsafe_b64encode(mac).rstrip(b"=").decode()


class Log:
    def __init__(self):
        self._lock = threading.Lock()
        self._seq = 0
        self._entries = []
        self.active = 0

    def begin(self, entry: dict) -> dict:
        with self._lock:
            self._seq += 1
            entry["seq"] = self._seq
            entry["activeAtStart"] = self.active
            self.active += 1
            self._entries.append(entry)
            if len(self._entries) > 2000:
                del self._entries[:500]
            return entry

    def end(self, entry: dict):
        with self._lock:
            self.active -= 1

    def since(self, seq: int):
        with self._lock:
            return [e for e in self._entries if e["seq"] > seq]

    def reset(self):
        with self._lock:
            self._entries.clear()


LOG = Log()


class ContentHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "drive-spike-source"

    def log_message(self, fmt, *args):  # eigenes, strukturiertes Logging statt Standardausgabe je Zeile
        pass

    def _send_json(self, status: int, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        split = urllib.parse.urlsplit(self.path)
        if split.path == "/healthz":
            return self._send_json(200, {"status": "ok"})

        decoded = urllib.parse.unquote(split.path)
        normalized = posixpath.normpath(decoded)
        match = PATH_RE.match(normalized)
        token = self.headers.get(TOKEN_HEADER)
        headers = {k: (v if k.lower() != TOKEN_HEADER.lower() else "<redacted>") for k, v in self.headers.items()}
        entry = LOG.begin({
            "ts": time.time(), "rawPath": split.path, "query": split.query, "normalizedPath": normalized,
            "headers": headers, "status": None, "bytesSent": 0, "aborted": False,
        })
        started = time.time()
        try:
            if not match:
                entry["status"] = 404
                return self._send_json(404, {"error": "unknown path"})
            delivery_id, item_id = match.group(1), match.group(2)
            entry["deliveryId"], entry["itemId"] = delivery_id, item_id
            token_ok = token is not None and hmac.compare_digest(token, expected_token(delivery_id))
            entry["tokenValid"] = token_ok
            if not token_ok:
                entry["status"] = 401
                return self._send_json(401, {"error": "invalid delivery token"})
            case = CASES.get(delivery_id.rsplit("-", 1)[-1])
            if case is None:
                entry["status"] = 404
                return self._send_json(404, {"error": "unknown delivery"})

            size = case["size"]
            params = urllib.parse.parse_qs(split.query)
            start = int(params.get("offset", ["0"])[0])
            length = int(params.get("length", [str(size - start)])[0])
            end = min(size, start + length)
            if start < 0 or start > size or end < start:
                entry["status"] = 416
                return self._send_json(416, {"error": "invalid range"})

            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Length", str(end - start))
            self.send_header("Content-Disposition", f'attachment; filename="{delivery_id}.bin"')
            self.end_headers()
            entry["status"] = 200
            rate = case.get("rate")
            for piece in content_slice(start, end):
                self.wfile.write(piece)
                entry["bytesSent"] += len(piece)
                if rate:
                    time.sleep(len(piece) / rate)
        except (BrokenPipeError, ConnectionResetError, TimeoutError):
            entry["aborted"] = True
        finally:
            entry["durationMs"] = int((time.time() - started) * 1000)
            LOG.end(entry)
            print(json.dumps({k: entry.get(k) for k in (
                "seq", "deliveryId", "itemId", "status", "tokenValid", "bytesSent", "aborted",
                "durationMs", "activeAtStart")} | {"edcBpn": headers.get("Edc-Bpn")}), flush=True)


class AdminHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _send_json(self, status: int, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        split = urllib.parse.urlsplit(self.path)
        if split.path == "/healthz":
            return self._send_json(200, {"status": "ok", "active": LOG.active})
        if split.path == "/_spike/requests":
            since = int(urllib.parse.parse_qs(split.query).get("since", ["0"])[0])
            return self._send_json(200, LOG.since(since))
        self._send_json(404, {"error": "unknown path"})

    def do_POST(self):
        if self.path == "/_spike/reset":
            LOG.reset()
            return self._send_json(200, {"status": "reset"})
        self._send_json(404, {"error": "unknown path"})


def serve(port: int, handler):
    server = ThreadingHTTPServer(("0.0.0.0", port), handler)
    server.daemon_threads = True
    server.serve_forever()


if __name__ == "__main__":
    threading.Thread(target=serve, args=(8091, AdminHandler), daemon=True).start()
    print(json.dumps({"event": "started", "cases": {k: v["size"] for k, v in CASES.items()}}), flush=True)
    serve(8090, ContentHandler)
