import { Component, inject, signal } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import { confirm } from 'devextreme/ui/dialog';
import notify from 'devextreme/ui/notify';

import { DriveApiService, TrashItem, errorText } from '../../core/drive-api.service';
import '../../core/devextreme-defaults';

@Component({
  selector: 'app-trash',
  imports: [DxDataGridModule, DxButtonModule],
  templateUrl: './trash.html',
  styleUrl: './trash.scss',
})
export class Trash {
  private readonly api = inject(DriveApiService);

  readonly items = signal<TrashItem[]>([]);
  readonly loading = signal(false);

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    this.loading.set(true);
    try {
      this.items.set(await this.api.trash());
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    } finally {
      this.loading.set(false);
    }
  }

  async restore(item: TrashItem): Promise<void> {
    try {
      const restored = await this.api.restore(item.id);
      notify(`„${restored.name}“ wurde wiederhergestellt.`, 'success', 3000);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  async purge(item: TrashItem): Promise<void> {
    const ok = await confirm(`„${item.name}“ endgültig löschen? Das lässt sich nicht rückgängig machen.`, 'Endgültig löschen');
    if (!ok) {
      return;
    }
    try {
      await this.api.purge(item.id);
      notify(`„${item.name}“ wurde endgültig gelöscht.`, 'success', 3000);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  formatType = (row: TrashItem): string => (row.isDirectory ? 'Ordner' : 'Datei');
}
