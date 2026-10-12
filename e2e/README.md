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
python3 e2e/phase1_acceptance.py --participant alice --large-bytes 0
```

Im Cluster mit 2-GB-Datei und Neustart. Das Skript läuft im Pod im Hintergrund, weil lange `kubectl exec`-Streams abreißen können:

```bash
kubectl apply -f e2e/k8s/acceptance-runner.yaml
kubectl -n windx-shared wait --for=condition=Ready pod/drive-acceptance
kubectl -n windx-shared exec -i drive-acceptance -- sh -c 'cat > /tmp/phase1_acceptance.py' < e2e/phase1_acceptance.py
kubectl -n windx-shared exec drive-acceptance -- sh -c \
  'setsid nohup python3 -u /tmp/phase1_acceptance.py --wait-for-restart > /tmp/run.log 2>&1 < /dev/null &'
kubectl -n windx-shared exec drive-acceptance -- cat /tmp/run.log   # Fortschritt
# sobald "Bereit: jetzt Drive und Datenbank neu starten" im Log steht:
kubectl -n windx-alice delete pod dataspace-drive-alice-postgres-0 --wait=false
kubectl -n windx-alice rollout restart deployment/dataspace-drive-alice
# nach "... Prüfungen bestanden":
kubectl delete -f e2e/k8s/acceptance-runner.yaml
```

Keycloak sperrt ein Konto kurz, wenn es sich mehrmals innerhalb einer Sekunde anmeldet (Brute-Force-Schutz). Das Skript meldet die parallelen Sitzungen deshalb gestaffelt an; die Arbeit danach läuft parallel.

Das Skript gibt keine Passwörter aus. Es braucht Python 3.9+ und `requests`.
