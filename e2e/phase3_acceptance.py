#!/usr/bin/env python3
"""Abnahme Phase 3 („Ausbau“) zwischen zwei laufenden DataspaceDrive-Instanzen, je Funktion:
  F1  Ordner teilen: der Empfänger erhält die Ordnerstruktur, Prüfsummen identisch
  F2  Neue Version teilen: Revision ersetzt die alte Zustellung, beim Empfänger eine neue Version derselben Datei
  F3  Vier-Augen-Prinzip: Freigabe wartet auf Prüfung, eine zweite Person gibt frei oder lehnt ab
Danach räumt das Skript auf (außer mit --keep); das Vier-Augen-Prinzip wird wieder auf den vorherigen Stand gesetzt.

  cd e2e && python3 phase3_acceptance.py
"""
from __future__ import annotations

import argparse
import hashlib
import os
import sys
import time

from drive_client import DriveClient, Failure, check, summary
from phase2_acceptance import BPNS, Exchange, wait_for


class Extended(Exchange):
    def folder(self, client: DriveClient, parent: str, name: str) -> str:
        return client.create_folder(parent, name)["id"]

    def children(self, client: DriveClient, folder_id: str) -> dict[str, dict]:
        return {i["name"]: i for i in client.children(folder_id)}

    def track_received(self, offer: dict):
        self.received_files += [i["fileEntryId"] for i in offer["items"] if i["fileEntryId"]]

    def f1_folder(self):
        root = self.a_user.personal_root()
        top = self.folder(self.a_user, root, f"Begehung {self.stamp}")
        fotos = self.folder(self.a_user, top, "Fotos")
        protocol, photo = os.urandom(5000), os.urandom(200_000)
        files = [self.a_user.upload_bytes(top, "Protokoll.txt", protocol), self.a_user.upload_bytes(fotos, "Rotor.jpg", photo)]
        self.sender_folders.append(top)
        for f in files:
            if self.a_user.wait_scanned(top if f["name"] == "Protokoll.txt" else fotos, f["id"]) != "Clean":
                raise Failure(f"{f['name']} nicht sauber")
        targets = {t["bpn"]: t["id"] for t in self.a_user.call("GET", "/api/partners/targets").json()}
        share = self.a_user.call("POST", "/api/shares", expect=(201,),
                                 json={"folderIds": [top], "partnerIds": [targets[BPNS[self.consumer]]]}).json()
        self.shares.append(share["id"])
        self.wait_active(share["id"])
        offer = self.wait_offer(share["title"])
        done = self.accept_and_wait(offer)
        received_top = self.children(self.b_user, self.b_user.personal_root())[share["title"]]
        self.received_folders.append(received_top["id"])
        inside = self.children(self.b_user, received_top["id"])
        photos = self.children(self.b_user, inside["Fotos"]["id"])
        got = self.b_user.download_sha(photos["Rotor.jpg"]["id"])[:2]
        if got != (len(photo), hashlib.sha256(photo).hexdigest()):
            raise Failure("Foto weicht ab")
        if self.b_user.download_sha(inside["Protokoll.txt"]["id"])[:2] != (len(protocol), hashlib.sha256(protocol).hexdigest()):
            raise Failure("Protokoll weicht ab")
        return f"„{share['title']}“ mit Unterordner Fotos übernommen ({len(done['items'])} Dateien), Prüfsummen identisch"

    def f2_revision(self):
        root = self.a_user.personal_root()
        name = f"Pflichtenheft {self.stamp}.pdf"
        first = self.a_user.upload_bytes(root, name, b"Revision eins")
        self.sender_files.append(first["id"])
        self.a_user.wait_scanned(root, first["id"])
        share = self.share([first["id"]], [self.consumer], name)
        self.wait_active(share["id"])
        self.track_received(self.accept_and_wait(self.wait_offer(name)))
        second = self.a_user.upload_bytes(root, name, b"Revision zwei")
        self.a_user.wait_scanned(root, second["id"])
        if not self.share_state(share["id"])["hasNewerVersions"]:
            raise Failure("Übersicht meldet keine neuere Version")
        revision = self.a_user.call("POST", f"/api/shares/{share['id']}/update", expect=(201,), json={"message": "neue Fassung"}).json()
        self.shares.append(revision["id"])
        self.wait_active(revision["id"])
        wait_for("alte Revision abgelöst", lambda: self.share_state(share["id"])["deliveries"][0]["state"] == "Revoked", 120)
        self.b_user.call("POST", "/api/inbox/refresh", expect=(202,))
        offer = wait_for("Revision im Eingang", lambda: next((o for o in self.b_user.call("GET", "/api/inbox").json()
                                                             if o["title"] == name and o["revision"] == 2 and o["state"] == "New"), None), 240, 4)
        if not offer["previousTargetFolderId"]:
            raise Failure("kein Ziel der letzten Übernahme")
        self.b_user.call("POST", f"/api/inbox/{offer['id']}/accept", json={"folderId": offer["previousTargetFolderId"]})
        done = wait_for("Revision übernommen", lambda: next((o for o in self.b_user.call("GET", "/api/inbox").json()
                                                            if o["id"] == offer["id"] and o["state"] == "Completed"), None), 600, 4)
        file_id = done["items"][0]["fileEntryId"]
        versions = self.b_user.call("GET", f"/api/files/{file_id}/versions").json()
        content = self.b_user.call("GET", f"/api/files/{file_id}/content").content
        if len(versions) != 2 or content != b"Revision zwei":
            raise Failure(f"{len(versions)} Versionen, Inhalt {content[:20]!r}")
        return "Revision 2 löst Revision 1 ab; beim Empfänger dieselbe Datei mit 2 Versionen, aktueller Inhalt identisch"

    def f3_approval(self):
        previous = self.a_admin.call("GET", "/api/admin/settings/sharing").json()["requireApproval"]
        self.restore_approval = previous
        self.a_admin.call("PUT", "/api/admin/settings/sharing", json={"requireApproval": True})
        root = self.a_user.personal_root()
        item = self.a_user.upload_bytes(root, f"vier-augen-{self.stamp}.txt", b"bitte pruefen")
        self.sender_files.append(item["id"])
        self.a_user.wait_scanned(root, item["id"])
        share = self.share([item["id"]], [self.consumer], f"Vier Augen {self.stamp}")
        if share["approval"] != "Pending" or share["deliveries"][0]["state"] != "Requested":
            raise Failure(f"Freigabe nicht wartend: {share['approval']}")
        time.sleep(8)
        if self.share_state(share["id"])["deliveries"][0]["state"] != "Requested":
            raise Failure("ohne Prüfung bereitgestellt")
        # alice.admin hat drive-approver; alice.nutzer nicht.
        self.a_user.call("POST", f"/api/approvals/{share['id']}/approve", expect=(403,))
        pending = self.a_admin.call("GET", "/api/approvals").json()
        if not any(p["id"] == share["id"] for p in pending):
            raise Failure("nicht in „Freigaben prüfen“")
        if self.a_admin.call("GET", f"/api/approvals/{share['id']}/items/1/content").content != b"bitte pruefen":
            raise Failure("Prüfer sieht den Inhalt nicht")
        self.a_admin.call("POST", f"/api/approvals/{share['id']}/approve")
        self.wait_active(share["id"])
        self.track_received(self.accept_and_wait(self.wait_offer(share["title"])))
        rejected = self.share([item["id"]], [self.consumer], f"Abgelehnt {self.stamp}")
        result = self.a_admin.call("POST", f"/api/approvals/{rejected['id']}/reject", json={"reason": "Abnahme"}).json()
        if result["approval"] != "Rejected" or result["deliveries"][0]["state"] != "Rejected":
            raise Failure("Ablehnung nicht wirksam")
        return "wartet ohne Prüfung; Absender kann nicht selbst freigeben; Prüfer sieht Inhalt, gibt frei → übernommen; Ablehnung beendet"

    def cleanup_extended(self):
        if getattr(self, "restore_approval", None) is not None:
            self.a_admin.call("PUT", "/api/admin/settings/sharing", json={"requireApproval": self.restore_approval})
        detail = self.cleanup()
        for client, folders in ((self.a_user, self.sender_folders), (self.b_user, self.received_folders)):
            for folder_id in folders:
                client.call("DELETE", f"/api/items/{folder_id}", expect=(200, 204, 404))
                client.call("DELETE", f"/api/trash/{folder_id}", expect=(200, 204, 404))
        return detail + f", {len(self.sender_folders) + len(self.received_folders)} Ordner gelöscht"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--provider", default="alice")
    parser.add_argument("--consumer", default="bob")
    parser.add_argument("--keep", action="store_true")
    args = parser.parse_args()
    print(f"Abnahme Phase 3: {args.provider} → {args.consumer}", flush=True)
    holder: dict = {}

    def login():
        x = Extended(args.provider, args.consumer, "dave")
        x.sender_folders, x.received_folders = [], []
        holder["x"] = x
        return "vier Konten angemeldet"

    if not check("Anmeldung", login):
        return summary()
    x: Extended = holder["x"]
    check("F1 Ordner teilen", x.f1_folder)
    check("F2 Neue Version teilen", x.f2_revision)
    check("F3 Vier-Augen-Prinzip", x.f3_approval)
    if not args.keep:
        check("Testdaten abräumen", x.cleanup_extended)
    return summary()


if __name__ == "__main__":
    sys.exit(main())
