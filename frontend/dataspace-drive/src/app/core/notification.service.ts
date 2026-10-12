import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface NotificationSummary {
  inboxNew: number | null;
  inboxAttention: number | null;
  approvalsPending: number | null;
  deliveriesFailed: number | null;
}

/** Zähler offener Arbeit für die Navigation, alle 60 Sekunden aktualisiert. */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private readonly http = inject(HttpClient);

  readonly summary = signal<NotificationSummary | null>(null);

  constructor() {
    const timer = setInterval(() => void this.refresh(), 60000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  async refresh(): Promise<void> {
    try {
      this.summary.set(await firstValueFrom(this.http.get<NotificationSummary>('/api/notifications/summary')));
    } catch {
      // Zähler sind Komfort; Fehler zeigt die jeweilige Seite selbst.
    }
  }
}
