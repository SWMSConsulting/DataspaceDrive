import { Component, inject, signal } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import notify from 'devextreme/ui/notify';

import { AdminApiService, AuditEntry } from '../../core/admin-api.service';
import { errorText } from '../../core/drive-api.service';

@Component({
  selector: 'app-activity',
  imports: [DxDataGridModule, DxButtonModule],
  templateUrl: './activity.html',
  styleUrl: './activity.scss',
})
export class Activity {
  private readonly api = inject(AdminApiService);

  readonly entries = signal<AuditEntry[]>([]);

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    try {
      this.entries.set(await this.api.audit(1000));
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  formatActor = (row: AuditEntry): string => row.actorName ?? 'System';
}
