import { Component, inject, signal } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import { DxPopupModule } from 'devextreme-angular/ui/popup';
import { DxTextAreaModule } from 'devextreme-angular/ui/text-area';
import notify from 'devextreme/ui/notify';

import { errorText } from '../../core/drive-api.service';
import { ShareSummary, SharingApiService } from '../../core/sharing-api.service';
import { downloadFile, formatBytes } from '../../shared/format';
import '../../core/devextreme-defaults';

/** Vier-Augen-Prinzip: Freigaben anderer prüfen, freigeben oder ablehnen. */
@Component({
  selector: 'app-approvals',
  imports: [DxDataGridModule, DxButtonModule, DxPopupModule, DxTextAreaModule],
  templateUrl: './approvals.html',
  styleUrl: './approvals.scss',
})
export class Approvals {
  private readonly api = inject(SharingApiService);

  readonly pending = signal<ShareSummary[]>([]);
  readonly rejecting = signal<ShareSummary | null>(null);
  readonly reason = signal('');

  readonly rejectButtonOptions = {
    text: 'Ablehnen',
    type: 'danger',
    stylingMode: 'contained',
    onClick: () => this.confirmReject(),
  };

  readonly cancelButtonOptions = {
    text: 'Abbrechen',
    stylingMode: 'contained',
    onClick: () => this.rejecting.set(null),
  };

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    try {
      this.pending.set(await this.api.approvals());
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  download(share: ShareSummary, itemKey: string): void {
    downloadFile(this.api.approvalContentUrl(share.id, itemKey));
  }

  async approve(share: ShareSummary): Promise<void> {
    try {
      await this.api.approve(share.id);
      notify(`„${share.title}“ ist freigegeben und wird bereitgestellt.`, 'success', 3000);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }

  startReject(share: ShareSummary): void {
    this.reason.set('');
    this.rejecting.set(share);
  }

  formatRecipients = (row: ShareSummary): string => row.deliveries.map((d) => d.partnerName).join(', ');
  formatSize = (row: ShareSummary): string => formatBytes(row.items.reduce((sum, i) => sum + i.size, 0));
  formatTitle = (row: ShareSummary): string => (row.revision > 1 ? `${row.title} (Revision ${row.revision})` : row.title);
  itemLabel = (item: { name: string; relativePath: string | null; size: number }): string =>
    `${item.relativePath ? item.relativePath + '/' : ''}${item.name} (${formatBytes(item.size)})`;

  private async confirmReject(): Promise<void> {
    const share = this.rejecting();
    if (!share) {
      return;
    }
    try {
      await this.api.reject(share.id, this.reason().trim());
      notify(`„${share.title}“ wurde abgelehnt.`, 'success', 3000);
      this.rejecting.set(null);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }
}
