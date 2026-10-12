import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { DxDrawerModule } from 'devextreme-angular/ui/drawer';
import { DxListModule } from 'devextreme-angular/ui/list';
import { DxToolbarModule } from 'devextreme-angular/ui/toolbar';
import { ItemClickEvent } from 'devextreme/ui/list';

import { AuthService, Me } from '../core/auth.service';
import { NotificationService, NotificationSummary } from '../core/notification.service';
import { ConfigService } from '../core/config.service';

interface NavItem {
  text: string;
  icon: string;
  path: string;
  /** Sichtbar nur, wenn die Bedingung für den angemeldeten Nutzer gilt. */
  visible?: (me: Me) => boolean;
  /** Zähler offener Arbeit hinter dem Text. */
  count?: (s: NotificationSummary) => number | null;
}

const allNavItems: NavItem[] = [
  { text: 'Dateien', icon: 'folder', path: '/files' },
  { text: 'Papierkorb', icon: 'trash', path: '/trash' },
  {
    text: 'Von mir geteilt',
    icon: 'share',
    path: '/shares',
    visible: (me) => me.canShareExternally || me.isAdmin,
    count: (s) => s.deliveriesFailed,
  },
  {
    text: 'Eingang',
    icon: 'download',
    path: '/inbox',
    visible: (me) => me.canManageInbox || me.isAdmin,
    count: (s) => (s.inboxNew ?? 0) + (s.inboxAttention ?? 0),
  },
  { text: 'Freigaben prüfen', icon: 'check', path: '/approvals', visible: (me) => me.canApprove, count: (s) => s.approvalsPending },
  { text: 'Partner', icon: 'group', path: '/partners', visible: (me) => me.isAdmin },
  { text: 'Aktivität', icon: 'clock', path: '/activity', visible: (me) => me.isAdmin },
  { text: 'Verwaltung', icon: 'preferences', path: '/admin', visible: (me) => me.isAdmin },
  { text: 'Einstellungen', icon: 'toolbox', path: '/settings' },
];

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, DxDrawerModule, DxToolbarModule, DxListModule],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  private readonly router = inject(Router);
  private readonly configService = inject(ConfigService);
  readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);

  readonly drawerOpen = signal(true);
  readonly participant = computed(() => this.configService.config()?.bpn ?? '');

  readonly navItems = computed(() => {
    const me = this.auth.me();
    const summary = this.notifications.summary();
    return allNavItems
      .filter((item) => !item.visible || (me !== null && item.visible(me)))
      .map((item) => {
        const count = summary && item.count ? (item.count(summary) ?? 0) : 0;
        return { ...item, text: count > 0 ? `${item.text} (${count})` : item.text };
      });
  });

  constructor() {
    void this.notifications.refresh();
  }

  readonly menuButtonOptions = {
    icon: 'menu',
    stylingMode: 'text',
    onClick: () => this.drawerOpen.update((open) => !open),
  };

  readonly logoutButtonOptions = {
    icon: 'runner',
    text: 'Abmelden',
    stylingMode: 'text',
    onClick: () => this.auth.logout(),
  };

  onNavItemClick(e: ItemClickEvent<NavItem>): void {
    const item = e.itemData;
    if (item?.path) {
      this.router.navigateByUrl(item.path);
      void this.notifications.refresh();
    }
  }
}
