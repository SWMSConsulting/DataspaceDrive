import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';

import { AuthService, Me } from '../core/auth.service';
import { ConfigService } from '../core/config.service';
import { NotificationService, NotificationSummary } from '../core/notification.service';
import { Icon } from '../shared/icon/icon';

interface NavItem {
  text: string;
  icon: string;
  path: string;
  /** Sichtbar nur, wenn die Bedingung für den angemeldeten Nutzer gilt. */
  visible?: (me: Me) => boolean;
  /** Zähler offener Arbeit als Badge. */
  count?: (s: NotificationSummary) => number | null;
}

interface NavGroup {
  title: string;
  items: NavItem[];
}

const navGroups: NavGroup[] = [
  {
    title: 'Ablage',
    items: [
      { text: 'Dateien', icon: 'folder', path: '/files' },
      { text: 'Papierkorb', icon: 'trash', path: '/trash' },
    ],
  },
  {
    title: 'Austausch',
    items: [
      {
        text: 'Von mir geteilt',
        icon: 'send',
        path: '/shares',
        visible: (me) => me.canShareExternally || me.isAdmin,
        count: (s) => s.deliveriesFailed,
      },
      {
        text: 'Eingang',
        icon: 'inbox',
        path: '/inbox',
        visible: (me) => me.canManageInbox || me.isAdmin,
        count: (s) => (s.inboxNew ?? 0) + (s.inboxAttention ?? 0),
      },
      { text: 'Freigaben prüfen', icon: 'shield', path: '/approvals', visible: (me) => me.canApprove, count: (s) => s.approvalsPending },
    ],
  },
  {
    title: 'Organisation',
    items: [
      { text: 'Partner', icon: 'building', path: '/partners', visible: (me) => me.isAdmin },
      { text: 'Aktivität', icon: 'activity', path: '/activity', visible: (me) => me.isAdmin },
      { text: 'Verwaltung', icon: 'sliders', path: '/admin', visible: (me) => me.isAdmin },
    ],
  },
];

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Icon],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  private readonly configService = inject(ConfigService);
  private readonly notifications = inject(NotificationService);
  readonly auth = inject(AuthService);

  /** Navigation auf schmalen Bildschirmen ausgeklappt. */
  readonly navOpen = signal(false);

  readonly organization = computed(() => {
    const config = this.configService.config();
    return config?.organization || config?.bpn || '';
  });

  readonly initials = computed(() => {
    const parts = (this.auth.me()?.name ?? '').split(/\s+/).filter(Boolean);
    return parts.slice(0, 2).map((p) => p[0]!.toUpperCase()).join('') || '?';
  });

  readonly userDetail = computed(() => this.auth.me()?.email || this.configService.config()?.bpn || '');

  readonly groups = computed(() => {
    const me = this.auth.me();
    const summary = this.notifications.summary();
    return navGroups
      .map((group) => ({
        title: group.title,
        items: group.items
          .filter((item) => !item.visible || (me !== null && item.visible(me)))
          .map((item) => ({ ...item, badge: summary && item.count ? (item.count(summary) ?? 0) : 0 })),
      }))
      .filter((group) => group.items.length > 0);
  });

  constructor() {
    void this.notifications.refresh();
    inject(Router)
      .events.pipe(
        filter((e) => e instanceof NavigationEnd),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe(() => {
        this.navOpen.set(false);
        void this.notifications.refresh();
      });
  }
}
