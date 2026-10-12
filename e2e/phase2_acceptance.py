#!/usr/bin/env python3
"""Abnahme Phase 2 („Teilen über den Dataspace“) zwischen zwei laufenden DataspaceDrive-Instanzen.

Testplan aus docs/konzept-dataspace-drive-de.md, Abschnitt 12:
  T1  Freigabe an einen Partner: Angebot im Eingang, Annahme, identischer SHA-256, Abruf beim Anbieter protokolliert
  T2  Freigabe an zwei Partner: jeder sieht nur seine Zustellung; Widerruf für einen lässt den anderen unberührt
  T3  große Datei (Standard 1 GB): ohne Timeout, Durchsatz dokumentiert
  T4  Ablauf (optional, --expiry, dauert gut 6 Minuten): Angebot verschwindet
  N4  Content-Pfad /edc/* ist über den Ingress nicht erreichbar
  N5  nach Widerruf: Angebot zurückgezogen, Annahme scheitert
  N7  Konto ohne drive-external-share: keine Freigabe möglich (403)
Danach widerruft das Skript seine Freigaben und löscht die Testdateien auf beiden Seiten (außer mit --keep).

Zugangsdaten: lokal aus dem Secret windx-shared-secrets (kubectl), im Cluster aus PW_<TEILNEHMER>_<NUTZER|ADMIN>.

  python3 e2e/phase2_acceptance.py                       # Alice → Bob, Dave als zweiter Empfänger
  python3 e2e/phase2_acceptance.py --large-bytes 0       # ohne große Datei
"""
from __future__ import annotations

import argparse
import hashlib
import os
import sys
import time
from datetime import datetime, timedelta, timezone

from drive_client import MIB, DriveClient, Failure, check, secret, summary

BPNS = {"alice": "BPNL00000000WA01", "bob": "BPNL00000000WB02", "dave": "BPNL00000000WD04"}
NAMES = {"alice": "Alice (windx-Demo)", "bob": "Bob (windx-Demo)", "dave": "Dave (windx-Demo)"}


def url(participant: str) -> str:
    return f"https://drive-{participant}-windx.cluster.swms-cloud.com"


def dsp(participant: str) -> str:
    return f"https://{participant}-edc-windx.cluster.swms-cloud.com/api/v1/dsp"


def password(participant: str, kind: str) -> str:
    env = os.environ.get(f"PW_{participant.upper()}_{kind.upper()}")
    return env if env else secret("windx-shared-secrets", f"{participant}-{'user' if kind == 'nutzer' else 'admin'}-password")


def wait_for(description: str, probe, timeout: float, interval: float = 3):
    """Ruft probe() auf, bis es etwas Wahres liefert; sonst Failure nach timeout Sekunden."""
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        last = probe()
        if last:
            return last
        time.sleep(interval)
    raise Failure(f"{description}: nach {timeout:.0f} s nicht erreicht (zuletzt {last!r})")


class Exchange:
    def __init__(self, provider: str, consumer: str, third: str):
        self.provider, self.consumer, self.third = provider, consumer, third
        self.stamp = time.strftime("%Y%m%d-%H%M%S")
        self.shares: list[str] = []
        self.sender_files: list[str] = []
        self.received_files: list[str] = []
        # Keycloak sperrt Konten bei mehreren Anmeldungen innerhalb einer Sekunde; gestaffelt anmelden.
        self.a_user = DriveClient(url(provider), f"{provider}.nutzer", password(provider, "nutzer"))
        time.sleep(1.2)
        self.a_admin = DriveClient(url(provider), f"{provider}.admin", password(provider, "admin"))
        time.sleep(1.2)
        self.b_user = DriveClient(url(consumer), f"{consumer}.nutzer", password(consumer, "nutzer"))
        time.sleep(1.2)
        self.b_admin = DriveClient(url(consumer), f"{consumer}.admin", password(consumer, "admin"))

    # --- Bausteine --------------------------------------------------------------------------------------------------

    @staticmethod
    def ensure_partner(admin: DriveClient, participant: str) -> dict:
        partners = admin.call("GET", "/api/admin/partners").json()
        partner = next((p for p in partners if p["bpn"] == BPNS[participant]), None)
        if partner is None:
            partner = admin.call("POST", "/api/admin/partners", expect=(201,),
                                 json={"bpn": BPNS[participant], "name": NAMES[participant], "dspAddress": dsp(participant)}).json()
        elif not partner["did"]:
            partner = admin.call("POST", f"/api/admin/partners/{partner['id']}/resolve").json()
        if not partner["did"]:
            raise Failure(f"{participant} nicht aufgelöst: {partner['resolveError']}")
        return partner

    def upload(self, name: str, size: int) -> tuple[dict, str]:
        digest = hashlib.sha256()

        def read(n: int) -> bytes:
            data = os.urandom(n)
            digest.update(data)
            return data

        root = self.a_user.personal_root()
        item = self.a_user.upload(root, name, size, read)
        self.sender_files.append(item["id"])
        status = self.a_user.wait_scanned(root, item["id"], timeout=1800)
        if status != "Clean":
            raise Failure(f"{name}: Scanstatus {status}")
        return item, digest.hexdigest()

    def share(self, file_ids: list[str], recipients: list[str], title: str, expires_at: datetime | None = None) -> dict:
        targets = {t["bpn"]: t["id"] for t in self.a_user.call("GET", "/api/partners/targets").json()}
        body = {"fileIds": file_ids, "partnerIds": [targets[BPNS[r]] for r in recipients], "title": title, "message": "Abnahme Phase 2"}
        if expires_at:
            body["expiresAt"] = expires_at.isoformat()
        share = self.a_user.call("POST", "/api/shares", expect=(201,), json=body).json()
        self.shares.append(share["id"])
        return share

    def share_state(self, share_id: str) -> dict:
        return next(s for s in self.a_user.call("GET", "/api/shares").json() if s["id"] == share_id)

    def wait_active(self, share_id: str) -> dict:
        def probe():
            share = self.share_state(share_id)
            if any(d["state"] == "ProvisioningFailed" for d in share["deliveries"]):
                raise Failure(f"Anlage fehlgeschlagen: {[d['lastError'] for d in share['deliveries']]}")
            return share if all(d["state"] == "Active" for d in share["deliveries"]) else None
        return wait_for("Zustellung aktiv", probe, 120)

    def find_offer(self, title: str) -> dict | None:
        return next((o for o in self.b_user.call("GET", "/api/inbox").json() if o["title"] == title), None)

    def wait_offer(self, title: str, state: str = "New", timeout: float = 240) -> dict:
        self.b_user.call("POST", "/api/inbox/refresh", expect=(202,))

        def probe():
            offer = self.find_offer(title)
            return offer if offer and offer["state"] == state else None
        return wait_for(f"Angebot „{title}“ im Zustand {state}", probe, timeout, interval=4)

    def accept_and_wait(self, offer: dict, timeout: float = 900) -> dict:
        self.b_user.call("POST", f"/api/inbox/{offer['id']}/accept", json={"folderId": self.b_user.personal_root()})

        def probe():
            current = self.find_offer(offer["title"])
            if current["state"] in ("Failed", "Quarantined"):
                raise Failure(f"Übernahme {current['state']}: {current['error']}")
            return current if current["state"] == "Completed" else None
        done = wait_for("Übernahme abgeschlossen", probe, timeout, interval=4)
        self.received_files += [i["fileEntryId"] for i in done["items"] if i["fileEntryId"]]
        return done

    def received_sha(self, offer: dict) -> tuple[int, str]:
        size, sha, _ = self.b_user.download_sha(offer["items"][0]["fileEntryId"])
        return size, sha

    # --- Prüfungen --------------------------------------------------------------------------------------------------

    def partners(self):
        a = self.ensure_partner(self.a_admin, self.consumer)
        d = self.ensure_partner(self.a_admin, self.third)
        b = self.ensure_partner(self.b_admin, self.provider)
        return f"{self.provider}: {a['did']}, {d['did']}; {self.consumer}: {b['did']}"

    def n7_role(self):
        self.a_admin.call("GET", "/api/partners/targets", expect=(403,))
        return f"{self.a_admin.user} ohne drive-external-share: 403"

    def t1(self):
        title = f"Abnahme T1 {self.stamp}"
        item, sha = self.upload(f"abnahme-t1-{self.stamp}.bin", 20 * MIB)
        started = time.time()
        share = self.share([item["id"]], [self.consumer], title)
        self.wait_active(share["id"])
        active_s = time.time() - started
        offer = self.wait_offer(title)
        visible_s = time.time() - started
        accepted = time.time()
        done = self.accept_and_wait(offer)
        transfer_s = time.time() - accepted
        if self.received_sha(done) != (20 * MIB, sha):
            raise Failure("SHA-256 beim Empfänger weicht ab")
        delivery = share["deliveries"][0]
        retrievals = self.a_user.call("GET", f"/api/deliveries/{delivery['id']}/retrievals").json()
        allowed = [r for r in retrievals if r["decision"] == "Allowed"]
        if not allowed or any(r["consumerBpn"] != BPNS[self.consumer] for r in allowed):
            raise Failure(f"Abrufprotokoll unvollständig: {retrievals[:3]}")
        count = self.share_state(share["id"])["deliveries"][0]["retrievalCount"]
        return (f"aktiv nach {active_s:.0f} s, im Eingang nach {visible_s:.0f} s, übernommen in {transfer_s:.0f} s; "
                f"SHA-256 identisch; {len(allowed)} Abschnitt(e) an {BPNS[self.consumer]}, {count} Abruf(e)")

    def t2(self):
        title = f"Abnahme T2 {self.stamp}"
        item, sha = self.upload(f"abnahme-t2-{self.stamp}.bin", MIB)
        share = self.wait_active(self.share([item["id"]], [self.consumer, self.third], title)["id"])
        self.wait_offer(title)
        offers = [o for o in self.b_user.call("GET", "/api/inbox").json() if o["title"] == title]
        if len(offers) != 1:
            raise Failure(f"{self.consumer} sieht {len(offers)} Angebote statt einem")
        third = next(d for d in share["deliveries"] if d["recipientBpn"] == BPNS[self.third])
        self.a_user.call("POST", f"/api/deliveries/{third['id']}/revoke")
        wait_for("Widerruf für den Dritten", lambda: next(d for d in self.share_state(share["id"])["deliveries"]
                                                          if d["id"] == third["id"])["state"] == "Revoked", 120)
        mine = next(d for d in self.share_state(share["id"])["deliveries"] if d["recipientBpn"] == BPNS[self.consumer])
        if mine["state"] != "Active":
            raise Failure(f"Zustellung an {self.consumer} ist {mine['state']}")
        done = self.accept_and_wait(offers[0])
        if self.received_sha(done) != (MIB, sha):
            raise Failure("SHA-256 weicht ab")
        return f"je Partner eine Zustellung; Widerruf für {self.third} ließ {self.consumer} unberührt, Übernahme identisch"

    def t3(self, size: int):
        title = f"Abnahme T3 {self.stamp}"
        started = time.time()
        item, sha = self.upload(f"abnahme-t3-{self.stamp}.bin", size)
        upload_s = time.time() - started
        share = self.wait_active(self.share([item["id"]], [self.consumer], title)["id"])
        offer = self.wait_offer(title)
        accepted = time.time()
        done = self.accept_and_wait(offer, timeout=3600)
        transfer_s = time.time() - accepted
        if self.received_sha(done) != (size, sha):
            raise Failure("SHA-256 weicht ab")
        segments = len(self.a_user.call("GET", f"/api/deliveries/{share['deliveries'][0]['id']}/retrievals").json())
        return (f"{size:,} Bytes: Upload+Scan {upload_s:.0f} s; Übernahme (Verhandlung, {segments} Abschnitte, Prüfung, Scan) "
                f"{transfer_s:.0f} s ≈ {size / MIB / max(transfer_s, 1):.0f} MiB/s; SHA-256 identisch").replace(",", ".")

    def n5(self):
        title = f"Abnahme N5 {self.stamp}"
        item, _ = self.upload(f"abnahme-n5-{self.stamp}.bin", 64 * 1024)
        share = self.wait_active(self.share([item["id"]], [self.consumer], title)["id"])
        offer = self.wait_offer(title)
        self.a_user.call("POST", f"/api/shares/{share['id']}/revoke")
        wait_for("Widerruf", lambda: self.share_state(share["id"])["deliveries"][0]["state"] == "Revoked", 120)
        self.wait_offer(title, state="Withdrawn")
        self.b_user.call("POST", f"/api/inbox/{offer['id']}/accept", expect=(409,), json={"folderId": self.b_user.personal_root()})
        return "Angebot nach Widerruf zurückgezogen, Annahme abgelehnt (409)"

    def n4(self):
        self.a_user.call("GET", "/edc/deliveries/00000000000000000000000000000000", expect=(404,))
        return "/edc/* über den Ingress: 404 (nur der Content-Port im Cluster liefert aus)"

    def t4_start(self):
        title = f"Abnahme T4 {self.stamp}"
        item, _ = self.upload(f"abnahme-t4-{self.stamp}.bin", 1024)
        share = self.share([item["id"]], [self.consumer], title, datetime.now(timezone.utc) + timedelta(minutes=6))
        self.wait_active(share["id"])
        self.wait_offer(title)
        return share, title

    def t4_finish(self, share: dict, title: str):
        wait_for("Ablauf", lambda: self.share_state(share["id"])["deliveries"][0]["state"] == "Expired", 600, interval=10)
        self.wait_offer(title, state="Withdrawn")
        return "nach Ablauf beendet, Angebot beim Empfänger zurückgezogen"

    def cleanup(self):
        for share_id in self.shares:
            self.a_user.call("POST", f"/api/shares/{share_id}/revoke")
        wait_for("alle Freigaben beendet", lambda: all(
            d["state"] in ("Revoked", "Expired") for s in self.shares for d in self.share_state(s)["deliveries"]), 180)
        for client, files in ((self.a_user, self.sender_files), (self.b_user, self.received_files)):
            for file_id in files:
                client.call("DELETE", f"/api/items/{file_id}", expect=(200, 204, 404))
                client.call("DELETE", f"/api/trash/{file_id}", expect=(200, 204, 404))
        return f"{len(self.shares)} Freigaben widerrufen, {len(self.sender_files) + len(self.received_files)} Testdateien gelöscht"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--provider", default="alice")
    parser.add_argument("--consumer", default="bob")
    parser.add_argument("--third", default="dave", help="zweiter Empfänger für T2 (braucht keine eigene Drive)")
    parser.add_argument("--large-bytes", type=int, default=1_000_000_000, help="Größe für T3, 0 = überspringen")
    parser.add_argument("--expiry", action="store_true", help="T4 (Ablauf) prüfen; dauert gut 6 Minuten")
    parser.add_argument("--keep", action="store_true", help="Testdaten stehen lassen")
    args = parser.parse_args()
    print(f"Abnahme Phase 2: {args.provider} → {args.consumer} (dritter Empfänger {args.third})", flush=True)

    holder: dict = {}

    def login():
        holder["x"] = Exchange(args.provider, args.consumer, args.third)
        return "vier Konten angemeldet"

    if not check("Anmeldung", login):
        return summary()
    x: Exchange = holder["x"]
    if not check("Partner eingerichtet und aufgelöst", x.partners):
        return summary()
    t4 = {}
    if args.expiry:
        check("T4 vorbereitet (Ablauf in 6 Minuten)", lambda: t4.update(zip(("share", "title"), x.t4_start())) or "läuft")
    check("N7 Teilen nur mit Rolle", x.n7_role)
    check("T1 Freigabe, Übernahme, Prüfsumme, Abrufprotokoll", x.t1)
    check("T2 Zwei Empfänger getrennt", x.t2)
    if args.large_bytes > 0:
        check("T3 Große Datei", lambda: x.t3(args.large_bytes))
    check("N5 Widerruf", x.n5)
    check("N4 Content-Pfad nicht über den Ingress", x.n4)
    if t4:
        check("T4 Ablauf", lambda: x.t4_finish(t4["share"], t4["title"]))
    if not args.keep:
        check("Testdaten abräumen", x.cleanup)
    return summary()


if __name__ == "__main__":
    sys.exit(main())
