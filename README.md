# DataspaceDrive

Dateien sicher über den Dataspace an andere Unternehmen freigeben: eine Web-Anwendung „wie Dropbox“, die jedes Unternehmen intern neben seinem EDC-Connector betreibt.

- Nutzer legen Dateien ab und geben sie an Partnerunternehmen frei.
- Jede Freigabe wird ein EDC-Angebot, das nur der adressierte Partner sieht.
- Der Partner übernimmt die Datei nach Annahme in seine eigene DataspaceDrive.

**Stand:** Phase 0 abgeschlossen (Konzept, Spike gegen den windx-Dataspace, Repo-Skelett). Details in [docs/konzept-dataspace-drive-de.md](docs/konzept-dataspace-drive-de.md) und [docs/spike-ergebnisse.md](docs/spike-ergebnisse.md).

## Aufbau

```
backend/                     .NET 10, ASP.NET Core ohne XAF
  src/DataspaceDrive.Api             Host: API/BFF, SPA, Content-Endpunkt für die EDC-Dataplane (Port 8090)
  src/DataspaceDrive.Core            Domäne: Statusmaschinen, Zugriffsprüfung, Zustell-Token
  src/DataspaceDrive.Infrastructure  Dateiablage (Dateisystem), später EF Core, ClamAV, Outbox
  src/Swms.Edc.Client                EDC-Management-Requests und Austauschprofil (im Spike verifiziert)
  tests/                             xUnit
frontend/dataspace-drive/    Angular 22 + DevExtreme 26.1
deploy/dataspace-drive/      Helm-Chart mit values-alice/bob/dave.yaml
spike/                       Spike-Skripte der Phase 0
docs/                        Konzept und Spike-Ergebnisse
```

## Lokal entwickeln

Backend (App-Port 5280, Content-Port 5290; ohne Schlüsselkonfiguration erzeugt die Entwicklungsumgebung einen flüchtigen Token-Schlüssel):

```bash
cd backend
dotnet test DataspaceDrive.slnx
dotnet run --project src/DataspaceDrive.Api
```

Frontend (leitet `/api` und `/healthz` an das Backend auf Port 5280 weiter):

```bash
cd frontend/dataspace-drive
npm ci
npm start
```

Konventionen:
- Komponenten mit getrennten `.ts`, `.html` und `.scss`.
- UI-Texte auf Deutsch.
- Die DevExtreme-Lizenz wird nur beim Image-Build über das Build-Argument `DEVEXTREME_LICENSE` in `src/license.ts` geschrieben, nie im Repo.

## Deployment

Image: `ghcr.io/swmsconsulting/dataspacedrive`, gebaut von [.github/workflows/build.yml](.github/workflows/build.yml).

Das Deployment je Teilnehmer übernimmt [.github/workflows/deploy.yml](.github/workflows/deploy.yml). Es braucht diese Secrets:

| Secret | Inhalt |
|---|---|
| `<TEILNEHMER>_EDC_API_KEY` | Management-API-Key des eigenen Connectors |
| `<TEILNEHMER>_DELIVERY_TOKEN_KEY` | Schlüssel für die Zustell-Tokens, Base64 mit mindestens 32 Bytes, z. B. `openssl rand -base64 32` |
| `KUBECONFIG_<TEILNEHMER>` oder `KUBECONFIG` | Base64-kodierte Kubeconfig |

Hinweise für das erste Deployment:
- Die NetworkPolicy lässt Port 8080 nur aus `ingress-nginx` und Port 8090 nur aus den Dataplane-Pods zu. Beim ersten Rollout prüfen, dass die HTTP-Probes des Kubelets durchkommen (Calico erlaubt Verkehr vom eigenen Node üblicherweise).
- Das PVC (`csi-cinder-high-speed-gen2-luks`) trägt `helm.sh/resource-policy: keep` und überlebt `helm uninstall`.
- Postgres, ClamAV und OIDC kommen mit Phase 1 ins Chart.

## Spike

`spike/run-spike.sh` prüft den Datenaustausch gegen den windx-Cluster:
- Die Testquelle läuft beim Anbieter, der Runner beim Empfänger, die Gegenprobe bei einem Dritten.
- Die Rollen werden über `PROVIDER`, `CONSUMER` und `NEGATIVE` gesetzt; Standard ist Alice → Bob, Gegenprobe Dave.
- Das Skript liest die Management-Keys aus den Control-Plane-Deployments und legt nur Objekte mit dem Präfix `drive-spike-<Lauf>` an.
- Am Ende räumt es auf. Assets mit Verträgen bleiben als Grabstein stehen, weil der EDC sie nicht löschen lässt.
