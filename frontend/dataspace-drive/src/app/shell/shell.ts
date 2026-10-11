import { Component, computed, inject, signal } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { DxDrawerModule } from 'devextreme-angular/ui/drawer';
import { DxListModule } from 'devextreme-angular/ui/list';
import { DxToolbarModule } from 'devextreme-angular/ui/toolbar';
import { ItemClickEvent } from 'devextreme/ui/list';

import { ConfigService } from '../core/config.service';

interface NavItem {
  text: string;
  icon: string;
  path: string;
}

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, DxDrawerModule, DxToolbarModule, DxListModule],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  private readonly router = inject(Router);
  private readonly configService = inject(ConfigService);

  readonly drawerOpen = signal(true);
  readonly participant = computed(() => this.configService.config()?.bpn ?? '');

  readonly navItems: NavItem[] = [
    { text: 'Dateien', icon: 'folder', path: '/files' },
    { text: 'Von mir geteilt', icon: 'share', path: '/shares' },
    { text: 'Eingang', icon: 'download', path: '/inbox' },
    { text: 'Partner', icon: 'group', path: '/partners' },
    { text: 'Aktivität', icon: 'clock', path: '/activity' },
    { text: 'Verwaltung', icon: 'preferences', path: '/admin' },
    { text: 'Einstellungen', icon: 'toolbox', path: '/settings' },
  ];

  readonly menuButtonOptions = {
    icon: 'menu',
    stylingMode: 'text',
    onClick: () => this.drawerOpen.update((open) => !open),
  };

  onNavItemClick(e: ItemClickEvent<NavItem>): void {
    const item = e.itemData;
    if (item?.path) {
      this.router.navigateByUrl(item.path);
    }
  }
}
