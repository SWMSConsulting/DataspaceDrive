import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxCheckBoxModule } from 'devextreme-angular/ui/check-box';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import { DxPopupModule } from 'devextreme-angular/ui/popup';
import { confirm } from 'devextreme/ui/dialog';
import notify from 'devextreme/ui/notify';

import { AuthService } from '../../core/auth.service';
import { errorText } from '../../core/drive-api.service';
import { IncomingItemInfo, IncomingOfferInfo, SharingApiService, offerInProgress, offerLabel } from '../../core/sharing-api.service';
import { FolderNode, FolderPicker } from '../../shared/folder-picker/folder-picker';
import { formatBytes, scanLabel } from '../../shared/format';

@Component({
  selector: 'app-inbox',
  imports: [DxDataGridModule, DxButtonModule, DxCheckBoxModule, DxPopupModule, FolderPicker],
  templateUrl: './inbox.html',
  styleUrl: './inbox.scss',
})
export class Inbox {
  private readonly api = inject(SharingApiService);
  readonly auth = inject(AuthService);

  readonly offers = signal<IncomingOfferInfo[]>([]);
  readonly showDone = signal(false);
  /** Erledigtes (abgelegt, abgelehnt, zurückgezogen, abgelaufen) nur auf Wunsch; Quarantäne und Fehler bleiben sichtbar. */
  readonly visibleOffers = computed(() =>
    this.showDone() ? this.offers() : this.offers().filter((o) => !['Completed', 'Declined', 'Withdrawn', 'Expired'].includes(o.state)),
  );
  readonly hiddenCount = computed(() => this.offers().length - this.visibleOffers().length);
  readonly acceptOffer = signal<IncomingOfferInfo | null>(null);
  readonly target = signal<FolderNode | null>(null);
  readonly canDecide = computed(() => !!this.auth.me()?.canManageInbox);
  readonly busy = computed(() => this.offers().some((o) => offerInProgress(o.state)));

  readonly acceptButtonOptions = {
    text: 'Annehmen',
    type: 'default',
    stylingMode: 'contained',
    onClick: () => this.confirmAccept(),
  };

  readonly cancelButtonOptions = {
    text: 'Abbrechen',
    stylingMode: 'outlined',
    onClick: () => this.acceptOffer.set(null),
  };

  constructor() {
    void this.reload();
    const timer = setInterval(() => {
      if (this.busy()) {
        void this.reload();
      }
    }, 4000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  async reload(): Promise<void> {
    try {
      this.offers.set(await this.api.inbox());
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  async checkNow(): Promise<void> {
    try {
      await this.api.refreshInbox();
      notify('Die Kataloge der Partner werden abgefragt.', 'info', 2500);
      setTimeout(() => void this.reload(), 4000);
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  startAccept(offer: IncomingOfferInfo): void {
    this.target.set(null);
    this.acceptOffer.set(offer);
  }

  async decline(offer: IncomingOfferInfo): Promise<void> {
    if (!(await confirm(`Angebot „${offer.title}“ von ${offer.partnerName} ablehnen?`, 'Ablehnen'))) {
      return;
    }
    await this.run(() => this.api.decline(offer.id), 'Das Angebot wurde abgelehnt.');
  }

  async retry(offer: IncomingOfferInfo): Promise<void> {
    await this.run(() => this.api.retryOffer(offer.id), 'Die Übernahme wird erneut versucht.');
  }

  onFolderSelected(folder: FolderNode | null): void {
    this.target.set(folder);
  }

  formatTitle = (row: IncomingOfferInfo): string => (row.revision > 1 ? `${row.title} (Aktualisierung, Revision ${row.revision})` : row.title);

  /** Revision einer schon übernommenen Freigabe: direkt in den Ordner der letzten Übernahme. */
  async acceptLikeLastTime(): Promise<void> {
    const offer = this.acceptOffer();
    if (!offer?.previousTargetFolderId) {
      return;
    }
    await this.run(
      () => this.api.accept(offer.id, offer.previousTargetFolderId!),
      `„${offer.title}“ wird nach „${offer.previousTargetPath}“ übernommen.`,
    );
    this.acceptOffer.set(null);
  }

  formatFrom = (row: IncomingOfferInfo): string =>
    row.senderOrganization && row.senderOrganization !== row.partnerName ? `${row.partnerName} (${row.senderOrganization})` : row.partnerName;
  formatFiles = (row: IncomingOfferInfo): string =>
    row.items.length === 1 ? row.items[0].name : `${row.items.length} Dateien`;
  formatSize = (row: IncomingOfferInfo): string => formatBytes(row.totalSize);
  formatState = (row: IncomingOfferInfo): string => {
    const label = offerLabel(row.state);
    if (row.state !== 'Downloading' || row.totalSize === 0) {
      return label;
    }
    const received = row.items.reduce((sum, i) => sum + i.receivedBytes, 0);
    return `${label} (${Math.floor((received / row.totalSize) * 100)} %)`;
  };
  formatItemProgress = (row: IncomingItemInfo): string =>
    row.size === 0 ? '' : `${Math.floor((row.receivedBytes / row.size) * 100)} %`;
  formatItemScan = (row: IncomingItemInfo): string => scanLabel(row.scanStatus);
  formatSize2 = (row: IncomingItemInfo): string => formatBytes(row.size);

  private async confirmAccept(): Promise<void> {
    const offer = this.acceptOffer();
    const target = this.target();
    if (!offer) {
      return;
    }
    if (!target) {
      notify('Bitte einen Zielordner wählen.', 'warning', 3000);
      return;
    }
    await this.run(() => this.api.accept(offer.id, target.id), `„${offer.title}“ wird nach „${target.path}“ übernommen.`);
    this.acceptOffer.set(null);
  }

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
