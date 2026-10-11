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

Backend (App-Port 5280, Content-Port 5290). Die Entwicklungsumgebung meldet einen festen Testnutzer an (`Auth:Mode=Development`), läuft ohne Virenscan und erzeugt einen flüchtigen Token-Schlüssel. Sie braucht ein lokales PostgreSQL; die Migration läuft beim Start:

```bash
docker run -d --name drive-postgres -p 5432:5432 -e POSTGRES_USER=drive -e POSTGRES_PASSWORD=drive -e POSTGRES_DB=drive postgres:16-alpine
cd backend
dotnet test DataspaceDrive.slnx          # API-Tests mit SQLite im Speicher; der PostgreSQL-Test braucht Docker
dotnet run --project src/DataspaceDrive.Api
```

Neue Migration: `dotnet tool restore && dotnet ef migrations add <Name> --project src/DataspaceDrive.Infrastructure --startup-project src/DataspaceDrive.Infrastructure --output-dir Persistence/Migrations`.

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

Das Deployment je Teilnehmer übernimmt [.github/workflows/deploy.yml](.github/workflows/deploy.yml); es braucht nur `KUBECONFIG_<TEILNEHMER>` oder `KUBECONFIG` (Base64). Die Geheimnisse liegen als Secret im Namespace und werden einmalig angelegt:

```bash
kubectl -n windx-<teilnehmer> create secret generic dataspace-drive-<teilnehmer>-secrets \
  --from-literal=edc-api-key='<Management-API-Key des eigenen Connectors>' \
  --from-literal=delivery-token-key-1="$(openssl rand -base64 32)"
```

Der Token-Schlüssel muss stabil bleiben: Er steckt in den `authCode`s aller veröffentlichten Zustell-Assets. Zum Rotieren einen zusätzlichen Schlüssel `delivery-token-key-2` anlegen und die aktuelle Version umstellen; alte Zustellungen bleiben gültig.

Im windx-Dataspace nutzen die Instanzen gemeinsame Demo-Dienste im Namespace `windx-shared`: einen Keycloak mit einem Realm je Unternehmen (`https://sso-windx.cluster.swms-cloud.com/realms/<teilnehmer>`) und einen ClamAV. `deploy/windx-shared/deploy.sh` legt sie an, erzeugt Zufalls-Passwörter und ergänzt in den Drive-Secrets `oidc-client-secret` und `postgres-password`. Die Demo-Zugangsdaten landen lokal in `deploy/windx-shared/zugangsdaten.local.md` (von Git ausgeschlossen). Beim Kunden zeigt `oidc.authority` auf dessen Identity Provider, und ClamAV läuft eingebettet (`clamav.enabled=true`) oder als vorhandener Dienst.

Hinweise für das erste Deployment:
- Die NetworkPolicy lässt Port 8080 nur aus `ingress-nginx` und Port 8090 nur aus den Dataplane-Pods zu. Beim ersten Rollout prüfen, dass die HTTP-Probes des Kubelets durchkommen (Calico erlaubt Verkehr vom eigenen Node üblicherweise).
- Das PVC (`csi-cinder-high-speed-gen2-luks`) trägt `helm.sh/resource-policy: keep` und überlebt `helm uninstall`.
- Die Drive bringt ihre eigene PostgreSQL mit (`postgres.enabled`, verschlüsseltes Volume); die Migration läuft beim Start.

## Spike

`spike/run-spike.sh` prüft den Datenaustausch gegen den windx-Cluster:
- Die Testquelle läuft beim Anbieter, der Runner beim Empfänger, die Gegenprobe bei einem Dritten.
- Die Rollen werden über `PROVIDER`, `CONSUMER` und `NEGATIVE` gesetzt; Standard ist Alice → Bob, Gegenprobe Dave.
- Das Skript liest die Management-Keys aus den Control-Plane-Deployments und legt nur Objekte mit dem Präfix `drive-spike-<Lauf>` an.
- Am Ende räumt es auf. Assets mit Verträgen bleiben als Grabstein stehen, weil der EDC sie nicht löschen lässt.
