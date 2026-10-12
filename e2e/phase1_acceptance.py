#!/usr/bin/env python3
"""Abnahme Phase 1 („Drive intern“) gegen eine laufende DataspaceDrive.

Prüft über die öffentliche URL (Ingress) mit echter Anmeldung beim Identity Provider:
  1. Anmeldung von Nutzer und Administrator (OIDC, Keycloak)
  2. Team-Bereich anlegen, beide sehen ihn mit Schreibrecht
  3. mehrere Sitzungen parallel: Ordner, Upload, Virenscan, Download mit SHA-256, Versionen,
     Umbenennen, Papierkorb, Suche
  4. gleichzeitige Uploads mit demselben Namen werden Versionen statt Fehler
  5. Trennung der persönlichen Bereiche
  6. Virenscan: EICAR bleibt gesperrt
  7. Upload über der Scannergrenze wird abgelehnt
  8. große Datei (Standard 2 000 000 000 Bytes) hoch- und herunterladen
  9. optional: Neustart von Drive und Datenbank ohne Datenverlust (--wait-for-restart; den Neustart löst
     man währenddessen selbst aus, z. B. kubectl rollout restart)
Zum Schluss räumt das Skript seine Testdaten wieder ab (außer mit --keep).

Zugangsdaten kommen aus der Umgebung (DRIVE_USER, DRIVE_PASSWORD, DRIVE_ADMIN, DRIVE_ADMIN_PASSWORD) oder,
mit --participant, aus dem Secret windx-shared-secrets der windx-Demo (kubectl nötig). Passwörter werden
nie ausgegeben.

  python3 e2e/phase1_acceptance.py --participant alice
  python3 e2e/phase1_acceptance.py --url https://drive.example.com --large-bytes 0
"""
from __future__ import annotations

import argparse
import hashlib
import os
import sys
import threading
import time
import warnings
from concurrent.futures import ThreadPoolExecutor

warnings.filterwarnings("ignore")
import requests  # noqa: E402

from drive_client import EICAR, MIB, DriveClient, Failure, check, report, secret, summary  # noqa: E402


def worker(client: DriveClient, team_root: str, index: int, rounds_files: int, max_bytes: int) -> str:
    folder = client.create_folder(team_root, f"Sitzung {index}")["id"]
    files = []
    for n in range(rounds_files):
        data = os.urandom(int.from_bytes(os.urandom(4), "big") % max_bytes + 1024)
        item = client.upload_bytes(folder, f"datei-{index}-{n}.bin", data)
        files.append((item, hashlib.sha256(data).hexdigest(), len(data)))
    for item, sha, size in files:
        status = client.wait_scanned(folder, item["id"])
        if status != "Clean":
            raise Failure(f"{item['name']}: Scanstatus {status}")
        got_size, got_sha, response = client.download_sha(item["id"])
        if (got_size, got_sha) != (size, sha):
            raise Failure(f"{item['name']}: Download weicht ab ({got_size} B)")
        if "attachment" not in response.headers.get("Content-Disposition", "") or response.headers.get("X-Content-Type-Options") != "nosniff":
            raise Failure(f"{item['name']}: Download nicht als Anhang mit nosniff")
    first, second, third = (f[0] for f in files[:3])
    v2 = client.upload_bytes(folder, first["name"], b"zweite Version")
    versions = client.call("GET", f"/api/files/{first['id']}/versions").json()
    if v2["id"] != first["id"] or len(versions) != 2:
        raise Failure(f"Versionierung: {len(versions)} Versionen")
    client.call("PATCH", f"/api/items/{second['id']}", json={"name": f"umbenannt-{index}.bin"})
    client.call("DELETE", f"/api/items/{third['id']}", expect=(200, 204))
    if not any(t["id"] == third["id"] for t in client.call("GET", "/api/trash").json()):
        raise Failure("gelöschte Datei fehlt im Papierkorb")
    client.call("POST", f"/api/trash/{third['id']}/restore", json={})
    hits = client.call("GET", "/api/search", params={"q": f"umbenannt-{index}"}).json()
    if not any(h["id"] == second["id"] for h in hits):
        raise Failure("Suche findet die umbenannte Datei nicht")
    return f"{client.user}: {rounds_files} Dateien, Version, Umbenennen, Papierkorb, Suche"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--participant", help="windx-Teilnehmer (alice, bob, ...); liest URL und Zugangsdaten aus dem Cluster")
    parser.add_argument("--url", default=os.environ.get("DRIVE_URL"))
    parser.add_argument("--sessions", type=int, default=6, help="parallele Sitzungen")
    parser.add_argument("--files", type=int, default=3, help="Dateien je Sitzung (mindestens 3)")
    parser.add_argument("--max-file-mb", type=float, default=12, help="Höchstgröße der Dateien je Sitzung in MiB")
    parser.add_argument("--large-bytes", type=int, default=2_000_000_000, help="Größe der großen Datei, 0 = überspringen")
    parser.add_argument("--wait-for-restart", action="store_true", help="auf einen Neustart warten und danach alles erneut prüfen")
    parser.add_argument("--keep", action="store_true", help="Testdaten nicht abräumen")
    args = parser.parse_args()

    if args.participant:
        p = args.participant
        base = args.url or f"https://drive-{p}-windx.cluster.swms-cloud.com"
        user, password = f"{p}.nutzer", secret("windx-shared-secrets", f"{p}-user-password")
        admin_user, admin_password = f"{p}.admin", secret("windx-shared-secrets", f"{p}-admin-password")
    else:
        base = args.url
        user, password = os.environ["DRIVE_USER"], os.environ["DRIVE_PASSWORD"]
        admin_user, admin_password = os.environ["DRIVE_ADMIN"], os.environ["DRIVE_ADMIN_PASSWORD"]
    if not base:
        parser.error("--url oder --participant angeben")
    print(f"Abnahme Phase 1 gegen {base}", flush=True)

    clients: dict[str, DriveClient] = {}
    state: dict = {}

    def login():
        clients["user"] = DriveClient(base, user, password)
        clients["admin"] = DriveClient(base, admin_user, admin_password)
        if not clients["admin"].me["isAdmin"] or clients["user"].me["isAdmin"]:
            raise Failure("Rollen stimmen nicht")
        return f"{user} und {admin_user} angemeldet, Rollen aus dem IdP übernommen"

    if not check("Anmeldung über den Identity Provider", login):
        return summary()
    u, a = clients["user"], clients["admin"]

    def team_space():
        name = f"Abnahme {time.strftime('%Y-%m-%d %H-%M-%S')}"
        space = a.call("POST", "/api/admin/spaces", expect=(201,),
                       json={"name": name, "acl": [{"principalType": "Role", "principalKey": "drive-user", "permission": "Write"}]}).json()
        state["space"] = space
        for client in (u, a):
            seen = next((s for s in client.spaces() if s["id"] == space["id"]), None)
            if not seen or seen["permission"] != "Write":
                raise Failure(f"{client.user} sieht den Bereich nicht mit Schreibrecht")
        return name

    if not check("Team-Bereich anlegen (Rolle drive-user: Schreiben)", team_space):
        return summary()
    team_root = state["space"]["rootFolderId"]

    def parallel():
        accounts = [(user, password), (admin_user, admin_password)]
        sessions = []
        for i in range(args.sessions):
            # Keycloak sperrt ein Konto kurz, wenn es sich schneller als einmal pro Sekunde anmeldet (Brute-Force-Schutz).
            time.sleep(1.2)
            sessions.append(DriveClient(base, *accounts[i % 2]))
        started = time.time()
        with ThreadPoolExecutor(max_workers=args.sessions) as pool:
            futures = [pool.submit(worker, client, team_root, i, max(args.files, 3), int(args.max_file_mb * MIB))
                       for i, client in enumerate(sessions)]
            details = [f.result() for f in futures]
        for client in (u, a):
            folders = {i["name"] for i in client.children(team_root) if i["isDirectory"]}
            if len(folders) != args.sessions:
                raise Failure(f"{client.user} sieht im Team-Bereich {len(folders)} statt {args.sessions} Ordner")
        return f"{args.sessions} Sitzungen in {time.time() - started:.0f} s, beide Nutzer sehen alle Ordner ({len(details)} fertig)"

    check("Mehrere Nutzer parallel", parallel)

    def same_name():
        folder = u.create_folder(team_root, "Gleichzeitig")["id"]
        rounds = 5
        for r in range(rounds):
            barrier = threading.Barrier(2)
            name = f"protokoll-{r}.txt"

            def upload(client, text):
                return client.upload_bytes(folder, name, text.encode(), before_complete=barrier.wait)

            with ThreadPoolExecutor(max_workers=2) as pool:
                items = [f.result() for f in [pool.submit(upload, u, f"von {user}"), pool.submit(upload, a, f"von {admin_user}")]]
            if items[0]["id"] != items[1]["id"] or sorted(i["versionNo"] for i in items) != [1, 2]:
                raise Failure(f"Runde {r}: {[(i['id'][:8], i['versionNo']) for i in items]}")
        return f"{rounds} Runden, jeweils eine Datei mit Version 1 und 2"

    check("Gleichzeitige Uploads mit demselben Namen", same_name)

    def isolation():
        root_a = a.personal_root()
        item = a.upload_bytes(root_a, "privat.txt", b"nur fuer den Admin")
        state["private"] = (root_a, item["id"])
        u.call("GET", f"/api/folders/{root_a}/children", expect=(404,))
        u.call("GET", f"/api/files/{item['id']}/content", expect=(404,))
        return "fremder persönlicher Bereich und dessen Dateien: 404"

    check("Persönliche Bereiche getrennt", isolation)

    def eicar():
        folder = u.create_folder(team_root, "Virenscan")["id"]
        bad = u.upload_bytes(folder, "eicar.com.txt", EICAR.encode())
        status = u.wait_scanned(folder, bad["id"])
        _, _, response = u.download_sha(bad["id"], expect=(403,))
        if status != "Infected":
            raise Failure(f"Status {status}")
        return f"Status {status}, Download 403 ({response.json().get('title', '')[:60]})"

    check("Virenscan sperrt EICAR", eicar)

    def over_limit():
        limit = u.call("GET", "/api/config").json()["maxUploadBytes"]
        r = u.call("POST", "/api/uploads", expect=(400,), json={"folderId": team_root, "fileName": "zu-gross.bin", "size": limit + 1})
        return f"Grenze {limit / MIB:.0f} MiB, darüber 400: {r.json().get('title', '')[:70]}"

    check("Upload über der Scannergrenze abgelehnt", over_limit)

    def large():
        size = args.large_bytes
        digest = hashlib.sha256()

        def read(n: int) -> bytes:
            data = os.urandom(n)
            digest.update(data)
            return data

        started = time.time()
        item = u.upload(team_root, "gross.bin", size, read)
        upload_s = time.time() - started
        state["large"] = (item["id"], digest.hexdigest(), size)
        started = time.time()
        status = u.wait_scanned(team_root, item["id"], timeout=1800)
        scan_s = time.time() - started
        if status != "Clean":
            raise Failure(f"Scanstatus {status}")
        started = time.time()
        got_size, got_sha, _ = u.download_sha(item["id"])
        download_s = time.time() - started
        if (got_size, got_sha) != (size, digest.hexdigest()):
            raise Failure("Download weicht ab")
        return (f"{size:,} Bytes: Upload {upload_s:.0f} s ({size / MIB / upload_s:.0f} MiB/s), Scan {scan_s:.0f} s, "
                f"Download {download_s:.0f} s, SHA-256 identisch").replace(",", ".")

    if args.large_bytes > 0:
        check("Große Datei", large)

    def restart():
        print("       Bereit: jetzt Drive und Datenbank neu starten (Warten bis 15 min) ...", flush=True)
        deadline, went_down = time.time() + 900, False
        while time.time() < deadline:
            try:
                ok = u.session.get(f"{base}/api/me", timeout=10).status_code == 200
            except requests.RequestException:
                ok = False
            if not ok:
                went_down = True
            elif went_down:
                break
            time.sleep(1)
        else:
            raise Failure("kein Neustart beobachtet" if not went_down else "Instanz kam nicht zurück")
        # Dieselbe Sitzung (Cookie) muss weiter gelten: Data-Protection-Schlüssel liegen in der Datenbank.
        u.call("GET", "/api/me")
        names = {i["name"] for i in u.children(team_root)}
        if not {"Gleichzeitig", "Virenscan"} <= names:
            raise Failure(f"Inhalt fehlt: {sorted(names)}")
        detail = "Sitzung gültig, Inhalte vorhanden"
        if "large" in state:
            file_id, sha, size = state["large"]
            if u.download_sha(file_id)[:2] != (size, sha):
                raise Failure("große Datei nach dem Neustart verändert")
            detail += ", große Datei unverändert"
        return detail

    if args.wait_for_restart:
        check("Neustart ohne Datenverlust", restart)

    def cleanup():
        space = state["space"]
        for item in u.children(team_root):
            u.call("DELETE", f"/api/items/{item['id']}", expect=(200, 204))
        # Der Papierkorb zeigt alles Gelöschte aus Bereichen mit Schreibrecht, also auch Einträge der anderen Sitzungen.
        for item in u.call("GET", "/api/trash").json():
            if item["spaceId"] == space["id"]:
                u.call("DELETE", f"/api/trash/{item['id']}", expect=(200, 204))
        if "private" in state:
            a.call("DELETE", f"/api/items/{state['private'][1]}", expect=(200, 204))
            a.call("DELETE", f"/api/trash/{state['private'][1]}", expect=(200, 204))
        a.call("DELETE", f"/api/admin/spaces/{space['id']}", expect=(204,))
        return "Team-Bereich und Testdateien entfernt"

    if not args.keep:
        check("Testdaten abräumen", cleanup)
    return summary()


if __name__ == "__main__":
    sys.exit(main())
