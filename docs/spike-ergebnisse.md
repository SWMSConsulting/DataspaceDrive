# Spike-Ergebnisse Phase 0

Stand: 11.10.2026. Der Spike hat die technischen Annahmen der DataspaceDrive gegen den laufenden windx-Dataspace geprüft, bevor Code für die Freigabelogik entsteht. Getestet wurde gegen Tractus-X EDC 0.12.1 (EDC 0.15.1) mit DSP 2025-1.

## Aufbau

- **Testquelle** (`spike/source/spike_source.py`) im Namespace des Anbieters. Sie simuliert den Content-Endpunkt der Drive:
  - Port 8090, HMAC-Zustell-Token im Header `X-Drive-Delivery-Token`
  - streamt deterministische Testdaten sofort
  - protokolliert alle eingehenden Header
  - Eine NetworkPolicy lässt auf 8090 nur die Dataplane-Pods zu.
- **Runner** (`spike/runner/spike_runner.py`) im Namespace des Empfängers.
  - Er legt beim Anbieter Policies, Zustell-Assets und Contract Definitions an.
  - Danach durchläuft er Katalog, Verhandlung, EDR und Download.
  - Ein dritter Teilnehmer dient als Gegenprobe.
- **Start:** `spike/run-spike.sh` (Rollen über `PROVIDER`, `CONSUMER`, `NEGATIVE`). Alle EDC-Objekte tragen das Präfix `drive-spike-<Lauf>`.
- **Läufe:**
  - Fünf Läufe insgesamt. Die ersten machten das Problem der Policy-Gleichheit sichtbar (S5) und zeigten zwei Konfigurationsabweichungen in der Testumgebung.
  - Beide Abweichungen sind behoben.
  - Referenz ist Lauf **10111714**: Alice als Anbieterin, Bob als Empfänger, Dave als Gegenprobe.

## Ergebnisse je Spike-Frage

| ID | Frage | Ergebnis |
|---|---|---|
| S1 | CX-Usage-Policy (FrameworkAgreement + UsagePurpose) | Anlage wird von der Policy-Validierung akzeptiert. Verhandelbar nur, wenn das DataExchangeGovernanceCredential des Empfängers `contractVersion "1.0"` trägt; der Connector vergleicht exakt. Im Testaufbau ist das nicht bei allen Teilnehmern der Fall. |
| S2 | Sichtbarkeit nur für den Empfänger | Bob sieht genau seine 6 Zustellungen, Dave genau seine eine. Daves Verhandlung mit Bobs Offer wird abgelehnt („Policy in scope catalog not fulfilled … BusinessPartnerNumber“). Die Access-Policy wird also auch bei der Verhandlung geprüft. |
| S3 | Katalogfilter auf eigene Properties | `querySpec.filterExpression` auf `drive:kind`, `drive:spikeRun` und `drive:recipientBpn` funktioniert über DSP 2025-1. |
| S4 | Was kommt am Backend an? | Siehe Liste unter der Tabelle. |
| S5 | Verhandlung über `/v3/edrs` | Siehe Liste unter der Tabelle. |
| S6 | Große Dateien, Parallelität, Segmente | **5 GiB in 189 s** (27 MiB/s, erstes Byte nach 0,03 s), Hash identisch; 1 GiB in 38 s. **12 parallele Downloads à 128 MiB** alle vollständig; erstes Byte nach bis zu 42 s, die Dataplane stellt also in eine Warteschlange. **4 Segmente à 8 MiB** über `?offset=&length=` ergeben zusammengesetzt den richtigen Hash. |
| S7 | Widerruf | Siehe Liste unter der Tabelle. |
| S8 | NetworkPolicy | Verbindung aus fremdem Pod zum Content-Port läuft in den Timeout; Calico/Canal setzt NetworkPolicies durch. |
| S9 | Asset mit Verträgen löschen | HTTP 409. Assets mit Agreements bleiben als „Grabstein“ stehen. |
| S13 | Umlaute in Titel und Manifest | Kommen unverändert im Katalog an. |

**S4 – Was kommt am Backend an?**
- `authKey`/`authCode` der dataAddress kommt als Header an.
- `Edc-Bpn` ist die **BPNL des Empfängers**, auch unter DSP 2025-1.
- `Edc-Contract-Agreement-Id` ist die **Agreement-ID des Anbieters**. Beim Anbieter liefert `GET /v3/contractagreements/{id}` das passende Asset.
- Vom Consumer selbst gesetzte Header (`Edc-Bpn`, `X-Spike-Probe`) werden **nicht** durchgereicht.
- `proxyPath` hängt Unterpfade an (`/items/1`).
- Traversal (`..%2F`, `/../`) erreicht keine fremde Zustellung; das Token gilt nur für eine Zustellung.

**S5 – Verhandlung über `/v3/edrs`**
- Das kompaktierte Offer aus dem DSP-2025-1-Katalog lässt sich **nicht** zurückschicken. Der Empfänger-EDC meldet „Policy in the contract agreement is not equal to the one in the contract offer“ (EDC `PolicyEquality` vergleicht Jackson-Bäume, ignoriert nur `@type`, `assignee` und `target`). Das gilt für alle getesteten Kontext-Varianten.
- Die **Profil-Form** funktioniert für alle fünf getesteten Contract-Policies auf Anhieb (FINALIZED): Policy in genau der Form, mit der der Anbieter sie angelegt hat, dazu nur die Offer-ID aus dem Katalog, `assigner` = DID des Anbieters und `target` = Asset.
- Katalog bis FINALIZED dauert etwa 4 s, das EDR liegt rund 2 s später vor.

**S7 – Widerruf**
- Das Agreement-Retirement bricht einen **laufenden Download nicht ab**; er lief noch 118 s bis zum Ende.
- Danach antwortet ein Abruf mit dem alten Token mit **403**, und der Transfer beim Empfänger ist TERMINATED.
- Nach dem Löschen der Contract Definition ist das Angebot aus dem Katalog verschwunden, und eine Neuverhandlung mit dem alten Offer scheitert.

## Beobachtungen zur Testumgebung

Zwei Konfigurationsabweichungen haben einzelne Läufe beeinflusst; beide sind behoben:
- Eine Dataplane referenzierte einen Signierschlüssel, der nicht im Vault lag. PULL-Transfers dieses Anbieters scheiterten deshalb.
- Ein Connector hatte eine Contract Definition mit **leerem `assetsSelector`** und offener Policy. Damit war jedes seiner Assets für alle Teilnehmer verhandelbar, unabhängig von BPN-Freigaben.

Die zweite Abweichung ist für die Drive die wichtigere Lehre (siehe Konsequenz 3 und 4).

## Konsequenzen für die Umsetzung

1. **Verhandeln über das Austauschprofil.** Der Empfänger baut die Contract-Policy aus dem Profil nach und übernimmt aus dem Katalog nur die Offer-ID. Umgesetzt in `Swms.Edc.Client` (`ExchangeProfile`, `EdcRequests.ContractRequest`), mit Golden-Test der im Spike erfolgreichen Form.
2. **Profil „membership“ als Standard** für die Contract-Policy; das CX-Profil folgt, wenn alle Teilnehmer passende DataExchangeGovernance-Credentials haben.
3. **Verteidigung in der Tiefe ist Pflicht.** Eine einzige Contract Definition mit leerem Selektor hebt den Katalogschutz auf. Der Content-Endpunkt prüft deshalb `Edc-Bpn` gegen den Empfänger und das Agreement im eigenen EDC (Asset muss passen).
4. **Reconciliation erkennt offene Contract Definitions** (leerer oder zu breiter Selektor) und meldet sie, bevor Zustellungen angelegt werden.
5. **Große Dateien segmentiert abrufen** (`offset`/`length`). So wirkt ein Widerruf ab dem nächsten Segment, und Abbrüche lassen sich fortsetzen.
6. **Parallelität beim Empfänger begrenzen** (Vorschlag: 2 Downloads je Anbieter, 4 insgesamt), weil die Dataplane bei 12 parallelen Abrufen bis zu 42 s puffert.
7. **Grabstein-Assets einplanen**: Nach Widerruf bleibt das Asset ohne Contract Definition stehen, die Agreements sind zurückgezogen.
8. **Zwei Agreement-IDs:** Der Anbieter sieht seine Agreement-ID, der Empfänger eine eigene `contractAgreementId`. Das Audit beider Seiten korreliert über Zustellung und Asset.

## Noch offen

S10–S12 und S14 (ClamAV, OIDC, Chunk-Upload über den Drive-Ingress, ein Anbieter mit eigenem Dataplane-Image) folgen in Phase 1.
