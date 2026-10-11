{{- define "drive.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "drive.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "drive.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "drive.labels" -}}
app.kubernetes.io/name: {{ include "drive.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "drive.selectorLabels" -}}
app.kubernetes.io/name: {{ include "drive.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "drive.secretName" -}}
{{- if .Values.existingSecret -}}{{ .Values.existingSecret }}{{- else -}}{{ include "drive.fullname" . }}{{- end -}}
{{- end -}}

{{- define "drive.claimName" -}}
{{- if .Values.storage.existingClaim -}}{{ .Values.storage.existingClaim }}{{- else -}}{{ include "drive.fullname" . }}-data{{- end -}}
{{- end -}}

{{- define "drive.clamavHost" -}}
{{- if .Values.clamav.enabled -}}{{ include "drive.fullname" . }}-clamav{{- else -}}{{ .Values.scanning.clamavHost }}{{- end -}}
{{- end -}}

{{/* Eigener Name für Nebenkomponenten, damit sie nie unter die Selektoren der Anwendung fallen. */}}
{{- define "drive.componentLabels" -}}
app.kubernetes.io/name: {{ include "drive.name" .root }}-{{ .component }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: {{ include "drive.name" .root }}
{{- end -}}

{{- define "drive.componentSelectorLabels" -}}
app.kubernetes.io/name: {{ include "drive.name" .root }}-{{ .component }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{/* clamd-Konfiguration: Limits passend zu scanning.maxStreamBytes, keine doppelte Signaturdatenbank beim Reload. */}}
{{- define "drive.clamdConf" -}}
Foreground yes
LogTime yes
LogClean no
DatabaseDirectory /var/lib/clamav
TemporaryDirectory /tmp
LocalSocket /tmp/clamd.sock
TCPSocket 3310
TCPAddr 0.0.0.0
MaxThreads 4
ReadTimeout 300
CommandReadTimeout 30
StreamMaxLength 4000M
MaxFileSize 4000M
MaxScanSize 4000M
ConcurrentDatabaseReload no
{{- end -}}
