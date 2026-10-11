import { ScanStatus } from '../core/drive-api.service';

const units = ['B', 'KB', 'MB', 'GB', 'TB'];

export function formatBytes(bytes: number): string {
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toLocaleString('de-DE', { maximumFractionDigits: unit === 0 ? 0 : 1 })} ${units[unit]}`;
}

const scanLabels: Record<ScanStatus, string> = {
  Pending: 'wird geprüft',
  Clean: 'geprüft',
  Infected: 'Virus gefunden',
  NotScannable: 'nicht prüfbar',
  Error: 'Prüffehler',
  NotScanned: 'ungeprüft',
};

export function scanLabel(status: ScanStatus | null): string {
  return status ? scanLabels[status] : '';
}

/** Startet den Download im Browser; das Sitzungs-Cookie authentifiziert den Request. */
export function downloadFile(url: string): void {
  const link = document.createElement('a');
  link.href = url;
  link.download = '';
  document.body.appendChild(link);
  link.click();
  link.remove();
}
