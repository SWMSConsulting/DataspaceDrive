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
