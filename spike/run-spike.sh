#!/usr/bin/env bash
# DataspaceDrive Spike: Testquelle beim Anbieter, Runner beim Empfaenger, Gegenprobe bei einem Dritten.
#
# Aufruf:   spike/run-spike.sh
# Optionen per Umgebung:
#   RUN_ID=0912a      Kennung des Laufs (Standard: MMDDHHMM), steckt in allen EDC-IDs
#   LARGE_MB=1024     Groesse der grossen Testdatei
#   PAR_MB=128        Groesse je parallelem Download
#   PAR_COUNT=12      Anzahl paralleler Downloads
#   CLEANUP=1         EDC-Objekte des Laufs am Ende loeschen (0 = stehen lassen)
#   KEEP_K8S=0        1 = Quelle und Runner nach dem Lauf nicht entfernen
#   PROVIDER=alice CONSUMER=bob NEGATIVE=dave   Rollen (alice, bob, dave)
#
# Voraussetzung: kubectl zeigt auf den windx-Cluster. Das Skript liest die Management-Keys aus den
# Control-Plane-Deployments und gibt sie nicht aus. Es aendert nichts an Vault, Connector-Konfiguration
# oder DataspaceOperator; es legt nur Objekte mit dem Praefix drive-spike-<RUN_ID> an.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
RUN_ID="${RUN_ID:-$(date +%m%d%H%M)}"
LARGE_MB="${LARGE_MB:-1024}"
PAR_MB="${PAR_MB:-128}"
PAR_COUNT="${PAR_COUNT:-12}"
CLEANUP="${CLEANUP:-1}"
KEEP_K8S="${KEEP_K8S:-0}"

# Rollen: Anbieter, Empfaenger, Gegenprobe. Der Anbieter darf keine Contract Definition mit leerem
# Selektor haben, sonst sind die Sichtbarkeits- und Widerrufstests nicht aussagekraeftig.
PROV="${PROVIDER:-alice}"; CONS="${CONSUMER:-bob}"; NEG="${NEGATIVE:-dave}"
bpn_of() {
  case "$1" in
    alice) echo BPNL00000000WA01 ;;
    bob)   echo BPNL00000000WB02 ;;
    dave)  echo BPNL00000000WD04 ;;
    *) echo "FEHLER: unbekannter Teilnehmer $1" >&2; exit 1 ;;
  esac
}
PROV_NS="windx-$PROV"; PROV_BPN="$(bpn_of "$PROV")"
CONS_NS="windx-$CONS"; CONS_BPN="$(bpn_of "$CONS")"
NEG_NS="windx-$NEG";   NEG_BPN="$(bpn_of "$NEG")"
PROV_DID="did:web:${PROV}-windx.cluster.swms-cloud.com"
PROV_DSP="https://${PROV}-edc-windx.cluster.swms-cloud.com/api/v1/dsp"
SOURCE_HOST="drive-spike-source.${PROV_NS}.svc.cluster.local"
RESULTS="$HERE/results"
mkdir -p "$RESULTS"

mgmt_key() { # $1 = Teilnehmer
  kubectl get deploy "$1-edc-controlplane" -n "windx-$1" \
    -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="WEB_HTTP_MANAGEMENT_AUTH_KEY")].value}'
}
mgmt_url() { echo "http://$1-edc-controlplane.windx-$1.svc.cluster.local:8081/management"; }
render() { # $1 = Manifest
  sed -e "s/__RUN_ID__/${RUN_ID}/g" -e "s/__LARGE_MB__/${LARGE_MB}/g" -e "s/__PAR_MB__/${PAR_MB}/g" \
      -e "s/__PAR_COUNT__/${PAR_COUNT}/g" -e "s/__CLEANUP__/${CLEANUP}/g" -e "s/__CONSUMER_NS__/${CONS_NS}/g" "$1"
}
apply_secret() { # $1 = Namespace, $2 = Name, Rest = --from-literal=...
  local ns="$1" name="$2"; shift 2
  kubectl -n "$ns" create secret generic "$name" "$@" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
}

echo "== Lauf ${RUN_ID}: Anbieter ${PROV}, Empfaenger ${CONS}, Gegenprobe ${NEG} (gross ${LARGE_MB} MiB, ${PAR_COUNT}x${PAR_MB} MiB parallel)"
PROV_KEY="$(mgmt_key "$PROV")"; CONS_KEY="$(mgmt_key "$CONS")"; NEG_KEY="$(mgmt_key "$NEG")"
for v in PROV_KEY CONS_KEY NEG_KEY; do
  [ -n "${!v}" ] || { echo "FEHLER: ${v} nicht gefunden (WEB_HTTP_MANAGEMENT_AUTH_KEY)"; exit 1; }
done
DELIVERY_KEY="$(openssl rand -hex 32)"

echo "== Testquelle in ${PROV_NS}"
kubectl -n "$PROV_NS" create configmap drive-spike-source --from-file=spike_source.py="$HERE/source/spike_source.py" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
apply_secret "$PROV_NS" drive-spike-source --from-literal=DELIVERY_KEY="$DELIVERY_KEY"
render "$HERE/k8s/source.yaml" | kubectl -n "$PROV_NS" apply -f - >/dev/null
kubectl -n "$PROV_NS" rollout restart deploy/drive-spike-source >/dev/null
kubectl -n "$PROV_NS" rollout status deploy/drive-spike-source --timeout=180s

echo "== Runner in ${CONS_NS}"
kubectl -n "$CONS_NS" create configmap drive-spike-runner --from-file=spike_runner.py="$HERE/runner/spike_runner.py" \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
apply_secret "$CONS_NS" drive-spike-runner \
  --from-literal=PROV_MGMT="$(mgmt_url "$PROV")" --from-literal=PROV_KEY="$PROV_KEY" \
  --from-literal=PROV_BPN="$PROV_BPN" --from-literal=PROV_DID="$PROV_DID" --from-literal=PROV_DSP="$PROV_DSP" \
  --from-literal=CONS_MGMT="$(mgmt_url "$CONS")" --from-literal=CONS_KEY="$CONS_KEY" --from-literal=CONS_BPN="$CONS_BPN" \
  --from-literal=NEG_MGMT="$(mgmt_url "$NEG")" --from-literal=NEG_KEY="$NEG_KEY" --from-literal=NEG_BPN="$NEG_BPN" \
  --from-literal=SOURCE_CONTENT="http://${SOURCE_HOST}:8090" --from-literal=SOURCE_ADMIN="http://${SOURCE_HOST}:8091" \
  --from-literal=DELIVERY_KEY="$DELIVERY_KEY"
kubectl -n "$CONS_NS" delete pod drive-spike-runner --ignore-not-found --wait=true >/dev/null
render "$HERE/k8s/runner.yaml" | kubectl -n "$CONS_NS" apply -f - >/dev/null

for _ in $(seq 1 60); do
  phase="$(kubectl -n "$CONS_NS" get pod drive-spike-runner -o jsonpath='{.status.phase}' 2>/dev/null || true)"
  [ "$phase" = "Pending" ] || [ -z "$phase" ] || break
  sleep 2
done
LOG="$RESULTS/${RUN_ID}.log"
kubectl -n "$CONS_NS" logs -f drive-spike-runner | tee "$LOG"
sed -n '/=====SPIKE-RESULT-JSON-BEGIN=====/,/=====SPIKE-RESULT-JSON-END=====/p' "$LOG" | sed '1d;$d' > "$RESULTS/${RUN_ID}.json"
kubectl -n "$PROV_NS" logs deploy/drive-spike-source > "$RESULTS/${RUN_ID}-source.log" 2>&1 || true
echo "== Ergebnisse: $LOG, $RESULTS/${RUN_ID}.json"

if [ "$KEEP_K8S" != "1" ]; then
  echo "== Entferne Spike-Pods und -Konfiguration"
  kubectl -n "$CONS_NS" delete pod/drive-spike-runner configmap/drive-spike-runner secret/drive-spike-runner \
    --ignore-not-found >/dev/null
  kubectl -n "$PROV_NS" delete deploy/drive-spike-source service/drive-spike-source networkpolicy/drive-spike-source \
    configmap/drive-spike-source secret/drive-spike-source --ignore-not-found >/dev/null
fi
