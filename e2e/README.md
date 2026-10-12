# Abnahme

`phase1_acceptance.py` prüft eine laufende DataspaceDrive über ihre öffentliche URL mit echter Anmeldung beim Identity Provider. Geprüft werden:

- parallele Sitzungen mehrerer Nutzer
- Team-Bereiche
- Versionen, Papierkorb und Suche
- gleichzeitige Uploads gleichen Namens
- Virenscan (EICAR)
- Scannergrenze
- eine große Datei
- optional ein Neustart ohne Datenverlust

Danach räumt das Skript seine Testdaten ab.

Lokal gegen die windx-Demo (Zugangsdaten aus dem Cluster, ohne große Datei):

```bash
cd e2e && python3 phase1_acceptance.py --participant alice --large-bytes 0
```

Im Cluster mit 2-GB-Datei und Neustart. Das Skript läuft im Pod im Hintergrund, weil lange `kubectl exec`-Streams abreißen können:

```bash
kubectl apply -f e2e/k8s/acceptance-runner.yaml
kubectl -n windx-shared wait --for=condition=Ready pod/drive-acceptance
for f in drive_client.py phase1_acceptance.py; do
  kubectl -n windx-shared exec -i drive-acceptance -- sh -c "cat > /tmp/$f" < e2e/$f
done
kubectl -n windx-shared exec drive-acceptance -- sh -c \
  'cd /tmp && setsid nohup python3 -u phase1_acceptance.py --wait-for-restart > /tmp/run.log 2>&1 < /dev/null &'
kubectl -n windx-shared exec drive-acceptance -- cat /tmp/run.log   # Fortschritt
# sobald "Bereit: jetzt Drive und Datenbank neu starten" im Log steht:
kubectl -n windx-alice delete pod dataspace-drive-alice-postgres-0 --wait=false
kubectl -n windx-alice rollout restart deployment/dataspace-drive-alice
# nach "... Prüfungen bestanden":
kubectl delete -f e2e/k8s/acceptance-runner.yaml
```

Keycloak sperrt ein Konto kurz, wenn es sich mehrmals innerhalb einer Sekunde anmeldet (Brute-Force-Schutz). Das Skript meldet die parallelen Sitzungen deshalb gestaffelt an; die Arbeit danach läuft parallel.

## Phase 2: Teilen über den Dataspace

`phase2_acceptance.py` prüft den Austausch zwischen zwei Drive-Instanzen nach dem Testplan im Konzept (Abschnitt 12):

- T1: Freigabe, Übernahme, Prüfsumme und Abrufprotokoll
- T2: zwei Empfänger getrennt
- T3: große Datei
- T4: Ablauf (`--expiry`)
- N4: Content-Pfad über den Ingress gesperrt
- N5: Widerruf
- N7: Teilen nur mit Rolle

Standard ist Alice → Bob, mit Dave als zweitem Empfänger (Dave braucht dafür keine eigene Drive).

```bash
cd e2e && python3 phase2_acceptance.py --large-bytes 0            # lokal, ohne große Datei
# im Cluster (Runner wie oben, beide Dateien kopieren):
for f in drive_client.py phase2_acceptance.py; do
  kubectl -n windx-shared exec -i drive-acceptance -- sh -c "cat > /tmp/$f" < e2e/$f
done
kubectl -n windx-shared exec drive-acceptance -- sh -c \
  'cd /tmp && setsid nohup python3 -u phase2_acceptance.py --expiry > /tmp/run2.log 2>&1 < /dev/null &'
```

Die Phase-1-Abnahme braucht im Pod ebenfalls `drive_client.py` neben `phase1_acceptance.py`.

Das Skript gibt keine Passwörter aus. Es braucht Python 3.9+ und `requests`.
