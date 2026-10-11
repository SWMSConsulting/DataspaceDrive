import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

/** Koordinaten dieser Instanz im Dataspace, wie sie das Backend unter /api/config liefert. */
export interface DriveConfig {
  participantId?: string;
  bpn?: string;
  dspAddress?: string;
  edcProfile?: string;
}

@Injectable({ providedIn: 'root' })
export class ConfigService {
  private readonly http = inject(HttpClient);

  readonly config = signal<DriveConfig | null>(null);
  readonly unavailable = signal(false);

  constructor() {
    this.http.get<DriveConfig>('/api/config').subscribe({
      next: (config) => this.config.set(config),
      error: () => this.unavailable.set(true),
    });
  }
}
