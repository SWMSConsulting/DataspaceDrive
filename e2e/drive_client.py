"""Gemeinsame Bausteine der Abnahmeskripte: Anmeldung über Keycloak, Drive-API, Ergebnisprotokoll."""
from __future__ import annotations

import base64
import hashlib
import html
import re
import subprocess
import threading
import time
import urllib.parse
import warnings

warnings.filterwarnings("ignore")
import requests  # noqa: E402

MIB = 1024 * 1024
# In zwei Hälften, damit diese Datei selbst keinen Virenscanner auslöst.
EICAR = "X5O!P%@AP[4\\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*"


class Failure(Exception):
    pass


results: list[tuple[bool, str, str]] = []
print_lock = threading.Lock()


def report(ok: bool, check: str, detail: str = "") -> None:
    with print_lock:
        results.append((ok, check, detail))
        print(f"{'OK    ' if ok else 'FEHLER'} {check}{' - ' + detail if detail else ''}", flush=True)


def secret(name: str, key: str) -> str:
    out = subprocess.run(["kubectl", "-n", "windx-shared", "get", "secret", name, "-o", f"jsonpath={{.data.{key}}}"],
                         capture_output=True, text=True, check=True).stdout
    return base64.b64decode(out).decode()


class DriveClient:
    """Eine Browser-Sitzung: OIDC-Anmeldung über die Keycloak-Seiten, danach Cookie und XSRF-Token."""

    def __init__(self, base: str, user: str, password: str):
        self.base = base.rstrip("/")
        self.user = user
        self.session = requests.Session()
        self._login(password)
        self.me = self.call("GET", "/api/me").json()

    def _login(self, password: str) -> None:
        r = self.session.get(f"{self.base}/api/auth/login", params={"returnUrl": "/"}, timeout=60)
        form = re.search(r'<form[^>]*action="([^"]*login-actions/authenticate[^"]*)"', r.text, re.I)
        if not form:
            raise Failure(f"{self.user}: keine Anmeldeseite des Identity Providers ({r.status_code})")
        r = self.session.post(html.unescape(form.group(1)), data={"username": self.user, "password": password, "credentialId": ""},
                              allow_redirects=False, timeout=60)
        if r.status_code == 200 and "signin-oidc" in r.text.lower():  # response_mode=form_post
            action = html.unescape(re.search(r'<form[^>]*action="([^"]+)"', r.text, re.I).group(1))
            fields = {html.unescape(n): html.unescape(v) for n, v in re.findall(r'<input[^>]*name="([^"]+)"[^>]*value="([^"]*)"', r.text, re.I)}
            r = self.session.post(action, data=fields, allow_redirects=False, timeout=60)
        elif r.status_code in (302, 303):
            r = self.session.get(r.headers["Location"], allow_redirects=False, timeout=60)
        if r.status_code not in (302, 303):
            raise Failure(f"{self.user}: Anmeldung fehlgeschlagen ({r.status_code})")

    def call(self, method: str, path: str, expect: tuple[int, ...] = (200,), timeout: float = 120, **kwargs) -> requests.Response:
        headers = kwargs.pop("headers", {})
        if method not in ("GET", "HEAD"):
            token = self.session.cookies.get("XSRF-TOKEN")
            if token:
                headers["X-XSRF-TOKEN"] = urllib.parse.unquote(token)
        r = self.session.request(method, self.base + path, headers=headers, timeout=timeout, **kwargs)
        if r.status_code not in expect:
            raise Failure(f"{method} {path}: {r.status_code} {r.text[:300]}")
        return r

    def spaces(self) -> list[dict]:
        return self.call("GET", "/api/spaces").json()

    def personal_root(self) -> str:
        return next(s["rootFolderId"] for s in self.spaces() if s["kind"] == "Personal")

    def children(self, folder_id: str) -> list[dict]:
        return self.call("GET", f"/api/folders/{folder_id}/children").json()["items"]

    def create_folder(self, parent_id: str, name: str) -> dict:
        return self.call("POST", "/api/folders", expect=(200, 201), json={"parentId": parent_id, "name": name}).json()

    def upload(self, folder_id: str, name: str, size: int, read, before_complete=None) -> dict:
        """read(n) liefert die nächsten n Bytes; before_complete() läuft unmittelbar vor dem Abschluss."""
        ticket = self.call("POST", "/api/uploads", expect=(200, 201), json={"folderId": folder_id, "fileName": name, "size": size}).json()
        chunk, index, sent = ticket["chunkSize"], 0, 0
        while sent < size or (size == 0 and index == 0):
            data = read(min(chunk, size - sent))
            self.call("PUT", f"/api/uploads/{ticket['uploadId']}/chunks/{index}", data=data,
                      headers={"Content-Type": "application/octet-stream"}, timeout=300)
            sent += len(data)
            index += 1
            if size == 0:
                break
        if before_complete:
            before_complete()
        return self.call("POST", f"/api/uploads/{ticket['uploadId']}/complete", json={"contentType": ""}, timeout=900).json()

    def upload_bytes(self, folder_id: str, name: str, data: bytes, before_complete=None) -> dict:
        view = memoryview(data)
        position = [0]

        def read(n: int) -> bytes:
            part = view[position[0]:position[0] + n].tobytes()
            position[0] += n
            return part

        return self.upload(folder_id, name, len(data), read, before_complete)

    def download_sha(self, file_id: str, expect: tuple[int, ...] = (200,)) -> tuple[int, str, requests.Response]:
        r = self.call("GET", f"/api/files/{file_id}/content", expect=expect, stream=True, timeout=900)
        digest, size = hashlib.sha256(), 0
        if r.status_code == 200:
            for part in r.iter_content(MIB):
                digest.update(part)
                size += len(part)
        else:
            _ = r.content  # Fehlertext für r.json() vorhalten
        r.close()
        return size, digest.hexdigest(), r

    def wait_scanned(self, folder_id: str, file_id: str, timeout: float = 600) -> str:
        deadline = time.time() + timeout
        while True:
            item = next(i for i in self.children(folder_id) if i["id"] == file_id)
            if item["scanStatus"] != "Pending":
                return item["scanStatus"]
            if time.time() > deadline:
                raise Failure(f"Virenscan von {item['name']} nach {timeout:.0f} s nicht fertig")
            time.sleep(2)


def check(name: str, func):
    try:
        detail = func()
        report(True, name, detail or "")
        return True
    except Exception as exc:  # noqa: BLE001 - jede Abweichung ist ein Befund
        report(False, name, str(exc))
        return False


def summary() -> int:
    failed = [r for r in results if not r[0]]
    print(f"\n{len(results) - len(failed)} von {len(results)} Prüfungen bestanden.", flush=True)
    return 1 if failed else 0
