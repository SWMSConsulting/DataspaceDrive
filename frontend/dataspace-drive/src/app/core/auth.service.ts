import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** Angemeldeter Nutzer, wie ihn /api/me liefert. */
export interface Me {
  id: string;
  name: string;
  email?: string;
  roles: string[];
  canShareExternally: boolean;
  canManageInbox: boolean;
  isAdmin: boolean;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  readonly me = signal<Me | null>(null);

  /** Lädt den Nutzer; ohne Sitzung leitet der Interceptor zur Anmeldung beim Identity Provider. */
  async load(): Promise<Me> {
    const me = await firstValueFrom(this.http.get<Me>('/api/me'));
    this.me.set(me);
    return me;
  }

  login(): void {
    const returnUrl = window.location.pathname + window.location.search;
    window.location.href = `/api/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  logout(): void {
    window.location.href = '/api/auth/logout';
  }
}
