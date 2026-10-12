import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxCheckBoxModule } from 'devextreme-angular/ui/check-box';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import { DxPopupModule } from 'devextreme-angular/ui/popup';
import { confirm } from 'devextreme/ui/dialog';
import notify from 'devextreme/ui/notify';

import { AuthService } from '../../core/auth.service';
import { errorText } from '../../core/drive-api.service';
import {
  DeliveryInfo,
  RetrievalInfo,
  ShareSummary,
  SharingApiService,
  deliveryInProgress,
  deliveryLabel,
} from '../../core/sharing-api.service';
import { formatBytes } from '../../shared/format';

const decisionLabels: Record<string, string> = {
  Allowed: 'ausgeliefert',
  InvalidToken: 'ungültiges Token',
  UnknownDelivery: 'unbekannt',
  NotActive: 'nicht aktiv',
  Expired: 'abgelaufen',
  PartnerBlocked: 'Partner gesperrt',
  MissingConsumerIdentity: 'ohne Empfängerkennung',
  WrongConsumer: 'falscher Empfänger',
  AgreementMismatch: 'Vertrag passt nicht',
};

@Component({
  selector: 'app-shares',
  imports: [DxDataGridModule, DxButtonModule, DxCheckBoxModule, DxPopupModule],
  templateUrl: './shares.html',
  styleUrl: './shares.scss',
})
export class Shares {
  private readonly api = inject(SharingApiService);
  readonly auth = inject(AuthService);

  readonly shares = signal<ShareSummary[]>([]);
  readonly showAll = signal(false);
  readonly showEnded = signal(false);
  /** Beendete Freigaben (alle Zustellungen widerrufen oder abgelaufen) nur auf Wunsch. */
  readonly visibleShares = computed(() =>
    this.showEnded()
      ? this.shares()
      : this.shares().filter((s) => s.deliveries.some((d) => !['Revoked', 'Expired', 'Rejected'].includes(d.state))),
  );
  readonly hiddenCount = computed(() => this.shares().length - this.visibleShares().length);
  readonly retrievals = signal<RetrievalInfo[] | null>(null);
  readonly retrievalsTitle = signal('');
  readonly busy = computed(() => this.shares().some((s) => s.deliveries.some((d) => deliveryInProgress(d.state))));

  constructor() {
    void this.reload();
    // Solange Zustellungen angelegt oder abgebaut werden, regelmäßig nachladen.
    const timer = setInterval(() => {
      if (this.busy()) {
        void this.reload();
      }
    }, 4000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  async reload(): Promise<void> {
    try {
      this.shares.set(await this.api.shares(this.showAll()));
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  toggleAll(value: boolean): void {
    this.showAll.set(value);
    void this.reload();
  }

  async revokeShare(share: ShareSummary): Promise<void> {
    if (!(await confirm(`Freigabe „${share.title}“ für alle Partner widerrufen?`, 'Freigabe widerrufen'))) {
      return;
    }
    await this.run(() => this.api.revokeShare(share.id), 'Die Freigabe wird widerrufen.');
  }

  async revokeDelivery(delivery: DeliveryInfo): Promise<void> {
    if (!(await confirm(`Freigabe für ${delivery.partnerName} widerrufen?`, 'Widerrufen'))) {
      return;
    }
    await this.run(() => this.api.revokeDelivery(delivery.id), `Die Freigabe für ${delivery.partnerName} wird widerrufen.`);
  }

  async retryDelivery(delivery: DeliveryInfo): Promise<void> {
    await this.run(() => this.api.retryDelivery(delivery.id), `Die Zustellung an ${delivery.partnerName} wird erneut angelegt.`);
  }

  async showRetrievals(delivery: DeliveryInfo): Promise<void> {
    try {
      this.retrievals.set(await this.api.retrievals(delivery.id));
      this.retrievalsTitle.set(`Abrufprotokoll – ${delivery.partnerName}`);
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  canRevoke = (d: DeliveryInfo): boolean => ['Active', 'Provisioning', 'ProvisioningFailed'].includes(d.state);
  canRevokeShare = (s: ShareSummary): boolean => s.deliveries.some(this.canRevoke);

  formatFiles = (row: ShareSummary): string => row.items.map((i) => i.name).join(', ');
  formatSize = (row: ShareSummary): string => formatBytes(row.items.reduce((sum, i) => sum + i.size, 0));
  formatRecipients = (row: ShareSummary): string =>
    row.deliveries.map((d) => `${d.partnerName}: ${deliveryLabel(d.state)}`).join(' · ');
  formatState = (row: DeliveryInfo): string => deliveryLabel(row.state);
  formatDecision = (row: RetrievalInfo): string => decisionLabels[row.decision] ?? row.decision;
  formatRange = (row: RetrievalInfo): string =>
    row.offset === null && row.length === null ? 'ganze Datei' : `ab ${formatBytes(row.offset ?? 0)}, ${formatBytes(row.length ?? 0)}`;

  private async run(action: () => Promise<unknown>, success: string): Promise<void> {
    try {
      await action();
      notify(success, 'success', 3000);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }
}
