# Konzept: DataspaceDrive

Stand: 12.10.2026: Phase 1 („Drive intern“) und Phase 2 („Teilen über den Dataspace“) abgenommen, Phase 3 („Ausbau“) umgesetzt und bei Alice und Bob ausgerollt. Arbeitstitel analog zum DataspaceOperator.

## 1. Ziel

Der wind-x Dataspace läuft technisch:
- Alice, Bob und Dave betreiben Tractus-X EDC 0.12.1.
- Der DataspaceOperator stellt die Credentials aus und betreibt das BDRS.
- DSP 2025-1 funktioniert.

Was fehlt, ist eine Anwendung für normale Mitarbeitende. Heute braucht jeder Dateiaustausch jemanden, der Asset, Policy und Contract Definition anlegt, und auf der Gegenseite jemanden, der Katalog, Verhandlung und Transfer bedient. Die wind-x Use Cases brauchen genau diesen Austausch: UC4_1 (Wartungspflichtenheft, PDFs, rollenbasiertes Teilen) und UC4_2 (Begehungen, Fotos, Anhänge).

DataspaceDrive ist eine Web-Anwendung „wie Dropbox“, die jedes Unternehmen intern neben seinem EDC betreibt:
- Nutzer legen Dateien ab und geben sie gezielt an Partnerunternehmen frei.
- Jede Freigabe wird zu einem EDC-Angebot, das nur der adressierte Partner sieht und abrufen kann.
- Der Partner übernimmt die Datei nach Annahme in seine eigene DataspaceDrive.

## 2. Entscheidungen

| Thema | Entscheidung | Begründung |
|---|---|---|
| Backend | ASP.NET Core (.NET 10), modularer Monolith, **ohne XAF** | Kern sind Datei-Streaming, Chunk-Uploads, EDC-Orchestrierung und Ordnerrechte; dafür liefert XAF nichts. Die generierte Blazor-UI entfällt bei einem Angular-Frontend. Fieldservice (odata-/upload-api) und EdcAdmin folgen schon diesem Muster. Der DataspaceOperator zeigt die XAF-Kosten: Lizenzdatei im CI, EF-Core-8-Bindung, ~490-MB-Image, Deadlocks auf Blazor-Circuits. |
| Frontend | Angular 22 + DevExtreme 26.1 | Wie Fieldservice. `FileManager` mit `CustomFileSystemProvider`, `FileUploader` mit Chunks und `DataGrid` sind enthalten. Admin-Funktionen liegen als Bereich in derselben App. |
| Speicher | Dateisystem-Adapter auf verschlüsseltem PVC (`csi-cinder-high-speed-gen2-luks`); S3-Adapter später | Keine Zusatzdienste. Folge: eine Replik, Strategy `Recreate`, Hintergrunddienste im selben Prozess. |
| Empfang | Eingang mit manueller Annahme | Der Empfänger behält die Kontrolle; Prüfsumme und Virenscan laufen vor der Ablage. |
| Anmeldung | Generisches OIDC; für windx Keycloak 26 (Realm-Import wie im Fieldservice-Chart) | Kunden binden ihren eigenen IdP an. |
| Transfer | HttpData-PULL über die Tractus-X-EDR-API | Im Spike mit 1 GiB verifiziert. Uploads über die Dataplane gehen nicht, weil der Body als String gelesen wird. |
| Mandanten | Eine Instanz je Unternehmen (BPN) im Namespace neben dem EDC | Entspricht dem souveränen Teilnehmer im Dataspace. |

## 3. Ablauf

```mermaid
sequenceDiagram
    autonumber
    actor UA as Nutzer A
    participant DA as DataspaceDrive A
    participant EA as EDC A (Control/Data Plane)
    participant EB as EDC B
    participant DB as DataspaceDrive B
    actor UB as Nutzer B
    UA->>DA: Datei hochladen (Chunks), SHA-256
    UA->>DA: Teilen mit Partner B (Nachricht, Ablauf)
    DA->>EA: Asset drive-d-{id}, Access-Policy (BPN B), Contract Definition
    DB->>EB: Katalog von A abfragen (Filter drive:kind, drive:recipientBpn)
    EB->>EA: DSP 2025-1 Katalog (nur B sieht das Angebot)
    DB-->>UB: Angebot im Eingang
    UB->>DB: Annehmen (Zielordner)
    DB->>EB: POST /v3/edrs (Profil-Policy + Offer-ID)
    EB->>EA: Verhandlung + Transfer (HttpData-PULL)
    DB->>EA: GET Public API mit EDR-Token
    EA->>DA: GET :8090/edc/deliveries/{id} (Token, Edc-Bpn, Edc-Contract-Agreement-Id)
    DA-->>EA: Datei (Stream)
    EA-->>DB: Datei (Stream)
    DB->>DB: Quarantäne, SHA-256, ClamAV, Ablage "Von Partnern/A"
```

## 4. Anwendungen je Unternehmen

| Komponente | Art | Hinweise |
|---|---|---|
| `dataspace-drive` | **neu**, ein Image (API/BFF, SPA, Hintergrunddienste) | Port 8080 für UI/API über den Ingress (nur intern); Port 8090 Content-Endpunkt nur für die Dataplane (ClusterIP + NetworkPolicy) |
| PostgreSQL 16 (DB `drive`) | neu, Standard-Image | eigene Instanz mit LUKS-PVC oder extern; getrennt von der edc/ih-DB |
| ClamAV (`clamav/clamav`) | neu, Standard-Image | clamd TCP 3310, 2–3 GiB RAM, Limits für große Dateien anheben |
| PVC `dataspace-drive-data` | neu | `/data/blobs`, `/data/uploads`, `/data/quarantine` |
| Keycloak | nur windx/Demo | ein Realm je Unternehmen; Kunden nutzen ihren IdP |
| EDC, IdentityHub, Vault, DataspaceOperator, EdcAdmin | unverändert | höchstens Helm-Werte (z. B. Timeouts am Dataplane-Ingress) |

## 5. Projekte im Repo

```
backend/
  DataspaceDrive.slnx, Directory.Build.props, Directory.Packages.props (feste Versionen), global.json
  src/DataspaceDrive.Api             Host: REST-API, OIDC-BFF, SPA-Hosting, Content-Endpunkt :8090, Hintergrunddienste
  src/DataspaceDrive.Core            Domäne, Statusmaschinen, Zugriffsprüfung, Zustell-Token, Ports; frameworkfrei
  src/DataspaceDrive.Infrastructure  EF Core/Npgsql, Blob-Adapter, ClamAV, Outbox
  src/Swms.Edc.Client                EDC-Management-Client, Austauschprofil, Request-Builder, Versionsprofile
  tests/…                            xUnit; WebApplicationFactory; Golden-Tests der EDC-Requests
frontend/dataspace-drive/            Angular 22 + DevExtreme 26.1 (core/, shell/, features/)
deploy/dataspace-drive/              Helm-Chart, values-<teilnehmer>.yaml
spike/                               Spike-Skripte (Phase 0)
docs/                                Konzept, Spike-Ergebnisse
```

EdcAdmin wird nicht verändert. Wiederverwendbare Teile werden portiert: BDRS-Verzeichnis, DSP-Discovery, Download-Stream, Dockerfile, Workflows, Helm-Grundgerüst.

## 6. Austauschprofil v1 (EDC-Abbildung)

Das Austauschprofil ist der Vertrag zwischen zwei Drive-Instanzen. Beide Seiten müssen es kennen, weil der Empfänger die Contract-Policy beim Verhandeln in genau der Form des Anbieters zurückschickt (Spike S5).

### 6.1 Zustellungen

- **Zustellung:** Eine Freigabe an **ein** Partnerunternehmen ergibt ein eigenes Asset und eine eigene Contract Definition. Eine Freigabe an mehrere Partner erzeugt mehrere Zustellungen. Das hält Mitempfänger verborgen, ermöglicht Widerruf und Ablauf je Empfänger und ordnet Asset und BPN eindeutig zu.
- **IDs:** Asset `drive-d-{deliveryId}`, Contract Definition `drive-cd-{deliveryId}`, Access-Policy `drive-ap-{BPN}` (je Partner wiederverwendet), Contract-Policy `drive-cp-{profil}-v{n}` (unveränderlich, neue Versionen statt Änderungen).
- **Namensraum** der eigenen Properties: `https://ns.swms-cloud.com/drive/v1/`.
- **Öffentliche Asset-Properties:**
  - `name`, `dct:title`, `drive:kind=delivery`, `drive:schemaVersion`
  - `drive:recipientBpn`, `drive:senderBpn`, `drive:senderOrganization`
  - `drive:expiresAt`, `drive:itemCount`, `drive:totalSize`
  - `drive:contractProfile` (Abschnitt 6.2); bei Revisionen zusätzlich `drive:lineageId` und `drive:revision` (Abschnitt 6.7)
  - `drive:manifest`: JSON, ASCII-escaped, je Datei mit Name, Größe, SHA-256, MIME, Ordnerpfad (`path`) und Quellschlüssel (`source`)
- **Private Properties:** `drive:deliveryId`, `drive:instanceId`.
- **dataAddress:**
  - `type=HttpData`
  - `baseUrl=http://<release>-edc.<ns>.svc.cluster.local:8090/edc/deliveries/{deliveryId}`
  - `proxyPath=true`, `proxyQueryParams=true` (für `/items/{id}` und `offset`/`length`)
  - `authKey=X-Drive-Delivery-Token`, `authCode=v{n}.base64url(HMAC-SHA256(Schlüssel n, deliveryId))`
- HttpData statt ProxyHttpData, weil nur HttpData unter 0.12.1 die Header `Edc-Bpn` und `Edc-Contract-Agreement-Id` mitschickt.

### 6.2 Policies

- **Access-Policy:** Action `cx-policy:access`, `Membership eq active` UND `BusinessPartnerNumber isAnyOf [BPN]`, mit vollen IRIs und dem Kontext `https://w3id.org/dspace/2025/1/odrl-profile.jsonld`. Der Anbieter prüft sie beim Katalog und erneut bei der Verhandlung (S2).
- **Contract-Policy:** drei feste Vertragsprofile.
  - „open“ (`drive-cp-open-v1`, Standard): ohne Regeln. Den Empfänger legt allein die Access-Policy fest.
  - „membership“ (`drive-cp-membership-v1`): Action `cx-policy:access`, `Membership eq active`.
  - „cx“ (`drive-cp-cx-v1`): FrameworkAgreement `DataExchangeGovernance:1.0` und UsagePurpose `windx.drive.fileExchange:1`.
  - Die Instanz wählt das Profil (Helm-Wert `sharing.contractProfile`; die Verwaltung zeigt es an). Jede Freigabe schreibt ihr Profil ins Asset (`drive:contractProfile`). Der Empfänger verhandelt in genau dieser Form und übergeht Angebote mit unbekanntem Profil. Ohne Angabe gilt „membership“, damit Angebote älterer Instanzen weiter funktionieren.
  - „cx“ setzt voraus, dass die DataExchangeGovernance-Credentials aller Teilnehmer die vom Connector erwartete Version tragen (Spike S1).
  - **Warum „open“ der Standard ist:** Allgemeine EDC-Werkzeuge wie EdcAdmin schicken beim Verhandeln das Angebot aus dem Katalog zurück. Eine Contract-Policy mit Regeln kommt dort verkürzt an, und der Empfänger-EDC erkennt sie im Agreement nicht wieder (S5). Eine Policy ohne Regeln übersteht diesen Umweg unverändert.
  - Mitgliedschaft und BPN prüft der Anbieter schon über die Access-Policy, beim Katalog und bei der Verhandlung (S2). Der Content-Endpunkt prüft bei jedem Abruf zusätzlich BPN und Agreement. Die Regel „Membership“ in der Contract-Policy brachte darüber hinaus wenig.
  - Mit „open“ kann der Empfänger eine Freigabe mit genau einer Datei auch über EdcAdmin oder einen anderen EDC-Client abrufen. Ohne Datei-Schlüssel liefert der Content-Endpunkt nur bei genau einer Datei aus, mehrere Dateien gibt es nur über die Drive. Ein solcher Abruf umgeht beim Empfänger Prüfsumme, Virenscan und Ablage; der Anbieter protokolliert ihn wie jeden anderen.
- **Contract Definition:** `assetsSelector` mit `edc:id = drive-d-{deliveryId}`. Ein leerer Selektor ist im Client ausgeschlossen.

### 6.3 Verhandlung beim Empfänger

1. Discovery: `POST /v4alpha/connectordiscovery/dspversionparams` (0.13: `/v3/...`) liefert Protokoll, Adresse und DID.
2. Katalog mit `filterExpression` auf `drive:kind` und `drive:recipientBpn`, seitenweise bis zur leeren Seite.
3. `POST /v3/edrs` mit `@context [odrl-profile, {@vocab: edc, odrl}]`. Die Policy besteht aus `@id` (Offer-ID aus dem Katalog), `@type odrl:Offer`, `odrl:assigner {@id: DID des Anbieters}`, `odrl:target {@id: Asset}` und den Berechtigungen aus dem Profil.
4. Warten auf FINALIZED, danach EDR über `POST /v3/edrs/request` (Filter `contractNegotiationId`) und `GET /v3/edrs/{tp}/dataaddress?auto_refresh=true`.
5. Download per GET mit `Authorization: <token>` (ohne „Bearer“), segmentiert über `offset`/`length`, in die Quarantäne. Danach SHA-256 und Größe gegen das Manifest prüfen, ClamAV, Ablage, Transfer terminieren.

### 6.4 Content-Endpunkt

Der Content-Endpunkt (`GET :8090/edc/deliveries/{id}[/items/{itemId}]`) prüft in dieser Reihenfolge:
1. Anfrage kam über den Content-Port (`Connection.LocalPort`); die NetworkPolicy lässt nur Dataplane-Pods zu.
2. Das Zustell-Token ist gültig (HMAC, Vergleich mit konstanter Laufzeit, versionierte Schlüssel).
3. Die Zustellung ist `Active`, nicht abgelaufen, und der Partner ist nicht gesperrt.
4. `Edc-Bpn` entspricht der Empfänger-BPN.
5. `Edc-Contract-Agreement-Id` ist im eigenen EDC bekannt (`GET /v3/contractagreements/{id}`), nicht zurückgezogen, und das Asset passt.

Danach streamt der Endpunkt sofort, weil die Dataplane nach 30 s ohne Daten abbricht. Jede Ablehnung und jeder Abruf landen im Audit.

### 6.5 Widerruf und Ablauf

1. Status sofort auf `Revoking`; der Content-Endpunkt verweigert ab dann.
2. Contract Definition löschen.
3. Agreements des Assets über `POST /v3/contractagreements/retirements` zurückziehen (`@type AgreementsRetirementEntry`, `agreementId`, `tx:reason`).
4. Das Asset bleibt als Grabstein stehen, weil es mit Agreements nicht löschbar ist (409).

Ein laufender Download wird durch das Retirement nicht abgebrochen (S7). Bei segmentiertem Abruf wirkt der Widerruf ab dem nächsten Segment.

### 6.6 Robustheit

- EDC-Anlagen laufen über eine Outbox in derselben Transaktion wie die Fachänderung, mit deterministischen IDs. Eine Antwort 409 wird als „bereits angelegt“ behandelt und verglichen.
- Ein Reconciliation-Job gleicht `drive-*`-Objekte mit der Datenbank ab und meldet **offene Contract Definitions** (leerer oder zu breiter Selektor; im Spike beobachtet).
- `Swms.Edc.Client` kapselt alle Pfade und Formen. Das Versionsprofil `tractusx-0.12`/`tractusx-0.13` steuert die Unterschiede, etwa Discovery-Pfad und Identitäts-Header für alle Adresstypen ab 0.13.

### 6.7 Ordner und neue Versionen

- **Ordner:** Eine Freigabe kann Dateien und Ordner enthalten.
  - Für Ordner nimmt die Drive einen Schnappschuss der aktuellen, geprüften Versionen aller Dateien darin, bis 20 Ebenen tief und höchstens 200 Dateien je Freigabe.
  - Das Manifest trägt je Datei den Ordnerpfad (`path`). Der Empfänger baut die Struktur unter dem Zielordner nach und verwendet dabei gleichnamige Ordner weiter.
- **Neue Version teilen:** Eine Freigabe lässt sich mit den aktuellen Versionen derselben Dateien und Ordner erneuern.
  - Die Revision gehört zur selben Linie (`drive:lineageId`, `drive:revision`).
  - Die Zustellungen der Vorgängerin widerruft die Drive, sobald die Revision angelegt ist; mit Vier-Augen-Prinzip erst bei der Freigabe.
  - „Von mir geteilt“ zeigt an, wenn eine geteilte Datei inzwischen eine neuere Version hat.
- **Beim Empfänger:** Für eine Revision schlägt der Eingang den Zielordner der zuletzt übernommenen Revision vor („wie zuletzt“). Eine Datei mit demselben Quellschlüssel (`source`, die Datei-ID beim Absender) wird eine neue Version der zuvor empfangenen Datei statt einer neuen Datei.

### 6.8 Benachrichtigung über den Dataspace

Das Polling findet neue Angebote erst nach bis zu zwei Minuten. Damit sie sofort erscheinen, benachrichtigt der Absender den Empfänger über den Dataspace selbst:

1. **Eingangs-Asset:** Jede Drive bietet `drive-inbox-v1` an (`drive:kind=inbox`, HttpData auf `:8090/edc/notifications`, `proxyQueryParams=true`).
   - Sehen und verhandeln dürfen es nur die empfangsbereiten Partner: Access-Policy `drive-ap-inbox-v1` mit deren BPNs, Contract-Policy „membership“.
   - Die Drive gleicht Asset und Policy jede Minute mit der Partnerliste ab und spricht nur bei Änderungen mit dem Connector.
2. **Benachrichtigen:** Nach jeder aktivierten Zustellung verhandelt der Absender das Eingangs-Asset des Empfängers, einmal je Partner, und ruft es über dessen Dataplane ab: `GET …?event=delivery&asset=drive-d-{id}`.
   - Agreement und EDR werden wiederverwendet. Bei 401, 403, 404 oder 410 verhandelt die Drive neu.
3. **Empfangen:** Der Empfänger prüft das Token und `Edc-Bpn` (bekannter, empfangsbereiter Partner) und fragt sofort den Katalog dieses Partners ab.

Die Nachricht trägt kein Geheimnis. Sie löst nur eine Katalogabfrage aus, und was im Katalog steht, schützt weiterhin die Access-Policy. Die Benachrichtigung ist Komfort:
- höchstens fünf Versuche innerhalb von zehn Minuten, danach übernimmt das Polling
- jeder Schritt höchstens 30 s, damit ein hängender Partner die Anlage weiterer Zustellungen nicht aufhält

Die Partnerliste der Verwaltung zeigt je Partner die letzte gesendete und die letzte empfangene Benachrichtigung.

## 7. Domänenmodell

| Entität | Kern |
|---|---|
| User | OIDC-Subject, E-Mail, Name, zuletzt gesehene Rollen, E-Mail-Hinweise an/aus; beim ersten Login angelegt |
| Space / Folder | Bereich (persönlich, Team, Partner-Eingang); Ordnerbaum |
| FileEntry / FileVersion | unveränderliche Versionen mit Blob-Key, Größe, SHA-256, MIME, Scan-Status |
| UploadSession | Chunk-Upload mit Ablauf |
| AclEntry | Rechte je Bereich (später je Ordner): Lesen, Schreiben, Verwalten |
| Partner | BPN, DID, Name, DSP-Adresse, Quelle (BDRS/manuell), Freigabe- und Empfangsschalter, Sperre, automatische Annahme, Stand der Benachrichtigung |
| Share / ShareItem | Freigabe mit Titel, Nachricht, Ablauf, Linie und Revision, Vertragsprofil und Prüfung; Datei-Versionen als Schnappschuss mit Ordnerpfad |
| Delivery | Zustellung je Empfänger mit EDC-IDs, Token-Version, Status, Abrufzähler |
| KnownAgreement | Cache der Agreements für den Content-Endpunkt |
| IncomingOffer / IncomingItem | Eingang: Anbieter, Asset, Manifest, Verhandlung, Transfer, Prüfergebnis |
| OutboxMessage, AuditEvent | ausstehende EDC-Operationen; Audit nur anfügen, mit Hash-Kette |
| InstanceSetting | Einstellungen der Verwaltung zur Laufzeit (Vier-Augen-Prinzip) |
| MailMessage | E-Mail-Ausgang; ein Hintergrunddienst versendet über SMTP |

**Statusmaschinen** (umgesetzt in `DataspaceDrive.Core`):
- **Zustellung:** `Requested → Provisioning → Active → Revoking → Revoked`; `Active → Expiring → Expired`; `Provisioning → ProvisioningFailed → Provisioning | Revoking`; `Requested → Rejected`.
- **Prüfung einer Freigabe:** `NotRequired` oder `Pending → Approved | Rejected`; ohne Entscheidung bis zum Ablauf `Rejected`.
- **Eingang:** `New → Accepted → Negotiating → AwaitingTransfer → Downloading → Verifying → Scanning → Completed`. Daneben `Declined`, `Withdrawn` (kehrt nach `New` zurück, wenn das Angebot wieder auftaucht), `Expired`, `Failed` (Retry nach `Accepted`) und `Quarantined` (endgültig).

## 8. REST-API (Auszug)

| Bereich | Endpunkte |
|---|---|
| Session | `GET /api/auth/login`, `POST /api/auth/logout`, `GET /api/me`, `GET /api/config` |
| Dateien | `GET /api/folders/{id}/children`, `POST /api/folders`, `PATCH`/`DELETE /api/files/{id}`, `GET /api/files/{id}/content`, `GET /api/files/{id}/preview` |
| Upload | `POST /api/uploads`, `PUT /api/uploads/{id}/chunks/{index}`, `POST /api/uploads/{id}/complete` |
| Freigaben | `POST /api/shares` (Dateien und Ordner), `GET /api/shares`, `POST /api/shares/{id}/revoke`, `POST /api/shares/{id}/update` (neue Version), `POST /api/deliveries/{id}/revoke`, `POST /api/deliveries/{id}/retry`, `GET /api/deliveries/{id}/retrievals` |
| Prüfung | `GET /api/approvals`, `POST /api/approvals/{id}/approve`, `POST /api/approvals/{id}/reject`, `GET /api/approvals/{id}/items/{key}/content` |
| Eingang | `GET /api/inbox`, `POST /api/inbox/{id}/accept`, `POST /api/inbox/{id}/decline`, `POST /api/inbox/{id}/retry`, `POST /api/inbox/refresh` |
| Hinweise | `GET /api/notifications/summary` (Zähler der Navigation), `PUT /api/me/preferences` (E-Mail an/aus) |
| Verwaltung | Partner mit automatischer Annahme, `GET`/`PUT /api/admin/settings/sharing` (Vier-Augen-Prinzip, Vertragsprofil), Audit, EDC-Status und Abgleich |

## 9. Sicherheit

- **Anmeldung:** OIDC Code Flow mit PKCE im BFF; Tokens erreichen nie den Browser. HttpOnly-Cookie, Antiforgery-Token, Data-Protection-Keys in Postgres.
- **Rollen:**
  - `drive-user`
  - `drive-external-share`: darf an andere Unternehmen freigeben
  - `drive-inbox`: Eingang annehmen und ablehnen
  - `drive-admin`
  - `drive-approver`: prüft Freigaben, wenn das Vier-Augen-Prinzip eingeschaltet ist; die eigene Freigabe darf niemand selbst prüfen
- **Geheimnisse:**
  - Der EDC-Management-Key liegt nur serverseitig; die Drive beschränkt sich per Code auf `drive-*`-Objekte.
  - Der Schlüsselring der Zustell-Tokens kommt aus einem K8s-Secret. Ein Vault ist nicht nötig, das vermeidet die Abhängigkeit vom flüchtigen Vault.
- **Netz:** Port 8090 nur aus Dataplane-Pods, Port 8080 nur aus dem Ingress-Controller (beides NetworkPolicy; Calico/Canal setzt sie im Cluster durch, Spike S8).
- **Verschlüsselung:** LUKS-PVCs für Blobs und DB; TLS an Ingress, DSP und Public API.
- **Integrität und Schadcode:** SHA-256 beim Upload, im Manifest und beim Empfang. ClamAV bei Upload und Empfang; nicht prüfbare Dateien bleiben in der Quarantäne.
- **Datenschutz:** Titel, Nachricht, Dateinamen und Absenderorganisation sieht der Empfänger im Katalog. Die UI weist darauf hin; Personen werden standardmäßig nicht genannt.

## 10. Phasen

| Phase | Inhalt | Abnahme |
|---|---|---|
| 0 Konzept & Spike | Konzept, Spike, Repo-Skelett | **erledigt**, siehe `docs/spike-ergebnisse.md` |
| 1 Drive intern | OIDC, Bereiche/Ordner, Chunk-Upload, Download, Versionen, Papierkorb, ClamAV beim Upload, Helm/CI, Deployment Alice und Bob | **abgenommen** 12.10.2026 (`e2e/phase1_acceptance.py`): mehrere Nutzer parallel, 2-GB-Datei, Neustart ohne Datenverlust |
| 2 Teilen (MVP) | Partner, Freigabe-Dialog, Outbox/EDC-Anlage, Content-Endpunkt, Abrufprotokoll, Widerruf, Ablauf; Eingang mit Polling, Annahme, segmentiertem Download, Prüfung, Ablage; Audit; Reconciliation | **abgenommen** 12.10.2026: Testplan aus Abschnitt 12 mit `e2e/phase2_acceptance.py` im Cluster |
| 3 Ausbau | Ordner-Freigaben, neue Versionen geteilter Dateien, Vier-Augen-Prinzip, automatische Annahme, Vorschau, Hinweise in der Navigation und per E-Mail, Benachrichtigung über ein Eingangs-Asset, Vertragsprofile | **umgesetzt** 12.10.2026; je Funktion mit `e2e/phase3_acceptance.py` (Abschnitt 12) |
| 4 Härtung | S3-Adapter und mehrere Repliken, Backups, OpenTelemetry, Last- und Pentest, Profil 0.13, Kunden-Doku | Restore-Test, Update ohne Ausfall |

### Phase 3 im Einzelnen

| Funktion | Umsetzung |
|---|---|
| Ordner-Freigaben | Schnappschuss der Ordnerinhalte; der Empfänger baut die Struktur nach (Abschnitt 6.7) |
| Neue Version teilen | Revision derselben Linie löst die Vorgängerin ab; beim Empfänger eine neue Version derselben Datei (Abschnitt 6.7) |
| Vier-Augen-Prinzip | Die Verwaltung schaltet es zur Laufzeit ein (Vorgabe: Helm-Wert `sharing.requireApproval`). Freigaben warten dann in `Requested`, bis jemand mit `drive-approver` sie unter „Freigaben prüfen“ freigibt oder ablehnt. Die prüfende Person sieht die Inhalte und darf eigene Freigaben nicht prüfen. |
| Automatische Annahme | Je Partner: „aus“ (Standard); „nur Aktualisierungen“: Revisionen einer übernommenen Freigabe an den bisherigen Ort, im Namen der Person, die zuletzt angenommen hat; „alle“: zusätzlich neue Angebote in einen festen Ordner, im Namen der Person, die die Einstellung gesetzt hat. Prüfsumme und Virenscan laufen wie bei der manuellen Annahme. |
| Vorschau | Bilder (PNG, JPEG, GIF, WebP), PDF und Text (bis 2 MiB) im Browser, nur für geprüfte Dateien. Antwort `inline` mit `nosniff`, `no-store` und einer CSP mit `sandbox` (bei PDF nur `frame-ancestors`, weil der PDF-Viewer des Browsers sonst nicht startet). SVG, HTML und Unbekanntes: 415. |
| Hinweise | Zähler in der Navigation (neue Angebote, Eingang mit Handlungsbedarf, Freigaben zu prüfen, fehlgeschlagene Zustellungen). E-Mails über einen Ausgang in der Datenbank: neue Angebote an `drive-inbox`, Prüfanfragen an `drive-approver`, Entscheidung, erster Abruf und fehlgeschlagene Bereitstellung an die freigebende Person, Ergebnis der Übernahme an die annehmende Person. Abbestellen unter „Einstellungen“. Die windx-Demo fängt die E-Mails mit Mailpit ab. |
| Benachrichtigung über den Dataspace | Eingangs-Asset je Drive; neue Angebote erscheinen in Sekunden statt nach dem nächsten Polling (Abschnitt 6.8) |
| Vertragsprofile | „open“ (Standard), „membership“ und „cx“, je Instanz gewählt, im Asset angegeben (Abschnitt 6.2) |

## 11. Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| Offene Contract Definitions im Connector (leerer Selektor, im Spike beobachtet) heben den Katalogschutz auf | Prüfkette im Content-Endpunkt (BPN, Agreement); Reconciliation meldet offene Definitionen |
| Policy-Gleichheit beim Verhandeln | Profil-Form statt kompakter Katalog-Offers (S5), Golden-Tests |
| Version des DataExchangeGovernance-Credentials | Profil „open“ oder „membership“, bis alle Teilnehmer passende Credentials haben |
| Dataplane-Warteschlange bei vielen parallelen Abrufen | Begrenzung beim Empfänger, segmentierter Abruf, weitere Dataplane-Repliken |
| Laufender Download nach Widerruf | Segmente; Widerruf wirkt ab dem nächsten Segment |
| 30 s Leerlauf-Timeout der Dataplane | Content-Endpunkt streamt direkt vom Blob |
| Upgrade auf tractusx-edc 0.13 | Versionsprofil, Golden-Tests, E2E-Wiederholung |
| Eine Replik wegen RWO-PVC | Wartungsfenster; S3 in Phase 4 |
| Ein hängender Partner hält die Zustellungen auf (Benachrichtigung läuft in derselben Schleife) | jeder Schritt höchstens 30 s, höchstens fünf Versuche; das Polling bleibt die Grundlage |
| Rollen für E-Mail-Hinweise kennt die Drive erst nach einer Anmeldung | Rollen werden bei jeder Anmeldung übernommen; wer sich nie angemeldet hat, bekommt keine Hinweise |

## 12. E2E-Testplan

Phase 2 (`e2e/phase2_acceptance.py`):

| ID | Szenario | Erwartung |
|---|---|---|
| T1 | Freigabe an einen Partner | Angebot im Eingang; Annahme; identischer SHA-256; Abruf beim Anbieter protokolliert |
| T2 | Freigabe an zwei Partner | Jeder sieht nur seine Zustellung; Widerruf für einen lässt den anderen unberührt |
| T3 | 1 GB und 5 GB, 4 parallele Abrufe | ohne Timeout, Durchsatz dokumentiert |
| T4 | Ablauf | Angebot verschwindet; Content-Endpunkt antwortet 410 |
| N1 | Dritter Teilnehmer fragt den Katalog ab | Zustellung nicht sichtbar |
| N2 | Dritter verhandelt mit fremdem Offer | abgelehnt |
| N3 | Abruf mit gültigem Token, aber falscher `Edc-Bpn` oder fremdem Agreement | 403 und Audit |
| N4 | Port 8090 aus fremdem Pod, `/edc/*` über den Ingress | blockiert bzw. 404 |
| N5 | Nach Widerruf | altes Token 403, Neuverhandlung scheitert, Agreement zurückgezogen |
| N6 | EICAR beim Upload und beim Empfang | blockiert bzw. Quarantäne |
| N7 | Nutzer ohne `drive-external-share` | kein Teilen-Knopf, API 403 |

Phase 3 (`e2e/phase3_acceptance.py`):

| ID | Szenario | Erwartung |
|---|---|---|
| F1 | Ordner mit Unterordner teilen | Struktur beim Empfänger nachgebaut, Prüfsummen identisch |
| F2 | Neue Version einer geteilten Datei | „Von mir geteilt“ meldet die neuere Version; Revision 2 löst Revision 1 ab; beim Empfänger dieselbe Datei mit zwei Versionen |
| F3 | Vier-Augen-Prinzip | Freigabe wartet; der Absender kann nicht selbst freigeben (403); die prüfende Person sieht den Inhalt und gibt frei; eine Ablehnung beendet die Freigabe |
| F4 | Automatische Annahme | „alle“ legt im festen Ordner ab; „nur Aktualisierungen“ übernimmt Revision 2 ohne Rückfrage |
| F5 | Vorschau | PNG `inline` mit `sandbox` und `nosniff`; SVG 415 |
| F6 | Hinweise | Zähler „neue Angebote“ beim Empfänger |
| F7 | Benachrichtigung über den Dataspace | Angebot ohne „Jetzt prüfen“ in Sekunden im Eingang; Empfänger hat die Benachrichtigung vermerkt |

## 13. Abweichungen gegenüber dem freigegebenen Plan

| Plan | Jetzt | Grund |
|---|---|---|
| Ein Asset je Freigabe | ein Asset je Zustellung (Freigabe × Empfänger) | Mitempfänger verborgen, Widerruf je Empfänger, eindeutige BPN-Zuordnung |
| Auth-Header per Vault (`secretName`) | HMAC-Token je Zustellung als `authCode` | keine Abhängigkeit vom flüchtigen Vault; Token gilt nur für eine Zustellung |
| Content-Port 8081 | 8090 | Verwechslung mit der Dataplane (8081) vermeiden |
| Zugriffsprüfung über Token und BPN | zusätzlich Agreement-Prüfung im eigenen EDC | Spike: eine Contract Definition mit leerem Selektor hebt den Katalogschutz auf |
| Offer aus dem Katalog weiterreichen | Policy aus dem Austauschprofil, nur Offer-ID aus dem Katalog | Spike S5: kompakte Offers scheitern an der Policy-Gleichheit |
| Contract-Policy CX (FrameworkAgreement + UsagePurpose) | Profil „open“ (ohne Regeln) als Standard; „membership“ und „cx“ wählbar | Spike S1; mit Regeln in der Contract-Policy können allgemeine EDC-Werkzeuge wie EdcAdmin nicht verhandeln (S5), die Beschränkung auf den Empfänger leistet die Access-Policy |
| Download in einem Stück | segmentiert über `offset`/`length` | Spike S6/S7: fortsetzbar, Widerruf wirkt schneller |
| BDRS-Import der Partner (offen) | Partner pflegt die Verwaltung mit BPN und DSP-Adresse; DID und Protokoll löst der eigene Connector auf (`dspversionparams`) | Der Connector fragt das BDRS selbst; die Drive braucht kein STS-Secret. Die DID-Dokumente im windx-Dataspace tragen keinen DataService-Eintrag, daher bleibt die DSP-Adresse eine Angabe der Verwaltung. |
| Dateien bis 5 GB | mit Virenscan bis 2047 MiB | ClamAV prüft höchstens 2 GiB − 1 Byte; ungeprüfte Dateien bleiben gesperrt |
| 2 Abrufe je Anbieter, 4 insgesamt | der Empfänger übernimmt Angebote nacheinander | einfach und schont die Dataplane des Anbieters; Parallelität folgt bei Bedarf |
| Asset bleibt als Grabstein | ohne Agreements wird das Asset gelöscht, mit Agreements bleibt es ohne Contract Definition stehen | hält den Connector sauber, wo es geht (Spike S9) |
| Entität KnownAgreement | Agreements 30 s im Speicher zwischengespeichert | ein Widerruf ändert zuerst den Zustand der Zustellung und wirkt dadurch sofort |
| Audit mit Hash-Kette | Audit nur anfügen, ohne Hash-Kette | folgt mit der Härtung (Phase 4) |
| Freigabe aus jedem Bereich | aus Team-Bereichen nur, wenn die Verwaltung externes Teilen erlaubt; aus „Meine Dateien“ immer | Team-Inhalte gehören dem Team, nicht dem Einzelnen |
| Eingangs-Asset mit `proxyMethod`/`proxyBody` (kleines JSON) | `GET` mit Query-Parametern (`event`, `asset`) über `proxyQueryParams` | Die Nachricht besteht nur aus einer Asset-ID; derselbe, im Spike geprüfte PULL-Weg wie bei den Zustellungen, ohne Request-Body durch die Dataplane |
| Policy-Vorlagen | drei feste Vertragsprofile („open“, „membership“, „cx“), je Instanz gewählt | Der Empfänger muss die Policy in genau der Form des Anbieters zurückschicken (S5); frei gestaltete Vorlagen müssten beide Seiten kennen |
| Vorschau für PDF und Bilder, optional Collabora/OnlyOffice | Bilder, PDF und Text im Browser; keine Office-Vorschau | keine Zusatzdienste; SVG und HTML bleiben ausgeschlossen, weil sie Skripte enthalten können |
| optional Auto-Annahme | je Partner „aus“, „nur Aktualisierungen“ oder „alle“ in einen festen Ordner; Übernahme im Namen einer verantwortlichen Person | Rechte und Audit brauchen eine Person; Aktualisierungen bereits angenommener Freigaben sind der häufigste Fall |

## 14. Offene Entscheidungen

- Zeitpunkt für die Umstellung auf das Vertragsprofil „cx“. Das Profil ist umgesetzt und setzt passende DataExchangeGovernance-Credentials bei allen Teilnehmern voraus.
- Aufbewahrungsfristen für Audit und empfangene Dateien.
- Umgang mit Dateien über der Grenze des Virenscans (heute abgelehnt; denkbar: Freigabe ungeprüfter Dateien durch die Verwaltung).

Entschieden in Phase 2:
- Namensraum `https://ns.swms-cloud.com/drive/v1/`.
- Ablauf standardmäßig 30 Tage, höchstens 90; Dateigröße mit Virenscan bis 2047 MiB.
- Kein BDRS-Import in der Drive: die DID löst der Connector auf (siehe Abschnitt 13).

Entschieden in Phase 3:
- Das Vertragsprofil gilt je Instanz, nicht je Freigabe; Revisionen nehmen das zum Zeitpunkt gültige Profil.
- Standard ist „open“: Freigaben mit einer Datei lassen sich so auch mit EdcAdmin abrufen. Bestehende Zustellungen behalten ihre Policy, denn Policies werden nicht geändert, sondern neu versioniert.
- Die Benachrichtigung über den Dataspace ist Komfort; das Polling bleibt die Grundlage.
- Das Vier-Augen-Prinzip schaltet die Verwaltung zur Laufzeit; der Helm-Wert gibt nur den Anfangswert vor.
- Automatische Annahme ist standardmäßig aus; Prüfsumme und Virenscan laufen immer.
- E-Mails gehen über einen Ausgang in der Datenbank und lassen sich je Nutzer abbestellen.
