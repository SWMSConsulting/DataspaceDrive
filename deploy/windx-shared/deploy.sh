#!/usr/bin/env bash
# Gemeinsame Demo-Dienste der DataspaceDrive im windx-Dataspace: Keycloak (Realm je Unternehmen) und ClamAV
# im Namespace windx-shared. Idempotent: vorhandene Geheimnisse werden wiederverwendet.
#
# Aufruf:   deploy/windx-shared/deploy.sh
#   PARTICIPANTS="alice bob"   Unternehmen mit eigenem Realm und Drive-Instanz
#
# Geheimnisse entstehen zufällig und liegen nur im Cluster (Secrets windx-shared-secrets, keycloak-realms und
# dataspace-drive-<teilnehmer>-secrets) sowie lokal in zugangsdaten.local.md (von Git ausgeschlossen).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
NS=windx-shared
PARTICIPANTS="${PARTICIPANTS:-alice bob}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
umask 077

rand() { openssl rand -base64 48 | tr -d '/+=\n' | cut -c1-32; }
secret_value() { # $1 = Namespace, $2 = Secret, $3 = Schlüssel
  kubectl -n "$1" get secret "$2" -o "jsonpath={.data.$3}" 2>/dev/null | base64 -d 2>/dev/null || true
}
display_name() {
  case "$1" in
    alice) echo "Alice" ;; bob) echo "Bob" ;; dave) echo "Dave" ;; *) echo "$1" ;;
  esac
}

# apply statt "get || create": ein kurzer API-Fehler beim get führte sonst zu AlreadyExists und Abbruch.
kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f - >/dev/null
echo "== Geheimnisse in $NS"
if ! kubectl -n "$NS" get secret windx-shared-secrets >/dev/null 2>&1; then
  args=(--from-literal=keycloak-admin-password="$(rand)" --from-literal=keycloak-db-password="$(rand)")
  kubectl -n "$NS" create secret generic windx-shared-secrets "${args[@]}" >/dev/null
fi
for p in $PARTICIPANTS; do
  for key in client-secret user-password admin-password; do
    if [ -z "$(secret_value "$NS" windx-shared-secrets "$p-$key")" ]; then
      printf '{"stringData":{"%s":"%s"}}' "$p-$key" "$(rand)" > "$WORK/patch.json"
      kubectl -n "$NS" patch secret windx-shared-secrets --type merge --patch-file "$WORK/patch.json" >/dev/null
    fi
  done
done

echo "== Realms rendern"
realm_args=()
for p in $PARTICIPANTS; do
  python3 - "$HERE/realm-template.json" "$WORK/$p.json" "$p" "$(display_name "$p")" \
    "drive-$p-windx.cluster.swms-cloud.com" \
    "$(secret_value "$NS" windx-shared-secrets "$p-client-secret")" \
    "$(secret_value "$NS" windx-shared-secrets "$p-user-password")" \
    "$(secret_value "$NS" windx-shared-secrets "$p-admin-password")" <<'EOF'
import json, sys
template, target, realm, display, host, client, user_pw, admin_pw = sys.argv[1:9]
text = open(template, encoding="utf-8").read()
for key, value in {"__REALM__": realm, "__DISPLAY__": display, "__DRIVE_HOST__": host, "__CLIENT_SECRET__": client,
                   "__USER_PASSWORD__": user_pw, "__ADMIN_PASSWORD__": admin_pw}.items():
    text = text.replace(key, value)
json.loads(text)  # fehlerhafte Ersetzung sofort erkennen
open(target, "w", encoding="utf-8").write(text)
EOF
  realm_args+=(--from-file="$p.json=$WORK/$p.json")
done
kubectl -n "$NS" create secret generic keycloak-realms "${realm_args[@]}" --dry-run=client -o yaml | kubectl apply -f - >/dev/null

echo "== Manifeste anwenden"
applied="$(kubectl -n "$NS" apply -f "$HERE/manifests.yaml")"
# clamd liest seine Konfiguration nur beim Start.
if grep -q '^configmap/clamav configured' <<<"$applied"; then
  kubectl -n "$NS" rollout restart deployment/clamav >/dev/null
  echo "   ClamAV-Konfiguration geändert, clamd startet neu"
fi

echo "== Drive-Secrets der Teilnehmer ergänzen"
for p in $PARTICIPANTS; do
  ns="windx-$p"; name="dataspace-drive-$p-secrets"
  if ! kubectl -n "$ns" get secret "$name" >/dev/null 2>&1; then
    echo "   $name fehlt in $ns (siehe README, Abschnitt Deployment) - übersprungen"
    continue
  fi
  postgres="$(secret_value "$ns" "$name" postgres-password)"
  # Das Datenbank-Passwort darf sich nach der Initialisierung nicht mehr ändern.
  [ -n "$postgres" ] || postgres="$(rand)"
  printf '{"stringData":{"oidc-client-secret":"%s","postgres-password":"%s"}}' \
    "$(secret_value "$NS" windx-shared-secrets "$p-client-secret")" "$postgres" > "$WORK/patch.json"
  kubectl -n "$ns" patch secret "$name" --type merge --patch-file "$WORK/patch.json" >/dev/null
  echo "   $name: oidc-client-secret und postgres-password gesetzt"
done

echo "== Zugangsdaten lokal ablegen"
{
  echo "# Zugangsdaten windx-Demo (lokal, nicht versioniert)"
  echo
  echo "Keycloak-Admin: https://sso-windx.cluster.swms-cloud.com/admin - Nutzer admin, Passwort $(secret_value "$NS" windx-shared-secrets keycloak-admin-password)"
  echo
  for p in $PARTICIPANTS; do
    echo "## $(display_name "$p") - https://drive-$p-windx.cluster.swms-cloud.com"
    echo "- $p.nutzer / $(secret_value "$NS" windx-shared-secrets "$p-user-password") (drive-user, drive-external-share, drive-inbox)"
    echo "- $p.admin / $(secret_value "$NS" windx-shared-secrets "$p-admin-password") (drive-user, drive-admin)"
    echo
  done
} > "$HERE/zugangsdaten.local.md"
chmod 600 "$HERE/zugangsdaten.local.md"
echo "   $HERE/zugangsdaten.local.md"

echo "== Warten auf die Dienste (ClamAV lädt beim ersten Start die Signaturen)"
kubectl -n "$NS" rollout status statefulset/keycloak-postgres --timeout=300s
kubectl -n "$NS" rollout status deployment/keycloak --timeout=600s
kubectl -n "$NS" rollout status deployment/clamav --timeout=900s
