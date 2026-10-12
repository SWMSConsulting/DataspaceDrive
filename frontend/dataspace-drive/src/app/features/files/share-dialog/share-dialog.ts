import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DxDateBoxModule } from 'devextreme-angular/ui/date-box';
import { DxPopupModule } from 'devextreme-angular/ui/popup';
import { DxTagBoxModule } from 'devextreme-angular/ui/tag-box';
import { DxTextAreaModule } from 'devextreme-angular/ui/text-area';
import { DxTextBoxModule } from 'devextreme-angular/ui/text-box';
import notify from 'devextreme/ui/notify';

import { AuthService } from '../../../core/auth.service';
import { errorText } from '../../../core/drive-api.service';
import { ShareTarget, SharingApiService } from '../../../core/sharing-api.service';

export interface ShareFile {
  id: string;
  name: string;
  /** Ordner werden mit allen Unterordnern und Dateien geteilt. */
  isDirectory: boolean;
}

const day = 24 * 60 * 60 * 1000;

/** Dialog „An Partner freigeben“: Partner, Titel, Nachricht und Ablauf. */
@Component({
  selector: 'app-share-dialog',
  imports: [DxPopupModule, DxTagBoxModule, DxTextBoxModule, DxTextAreaModule, DxDateBoxModule],
  templateUrl: './share-dialog.html',
  styleUrl: './share-dialog.scss',
})
export class ShareDialog {
  private readonly api = inject(SharingApiService);
  readonly auth = inject(AuthService);

  /** Zu teilende Dateien; null schließt den Dialog. */
  readonly files = input<ShareFile[] | null>(null);
  readonly closed = output<boolean>();

  readonly targets = signal<ShareTarget[]>([]);
  readonly partnerIds = signal<string[]>([]);
  readonly title = signal('');
  readonly message = signal('');
  readonly expiresAt = signal<Date>(new Date(Date.now() + 30 * day));
  readonly saving = signal(false);

  readonly visible = computed(() => this.files() !== null);
  readonly minDate = new Date(Date.now() + day);
  readonly maxDate = new Date(Date.now() + 90 * day);

  readonly shareButtonOptions = {
    text: 'Freigeben',
    type: 'default',
    stylingMode: 'contained',
    onClick: () => this.share(),
  };

  readonly cancelButtonOptions = {
    text: 'Abbrechen',
    stylingMode: 'outlined',
    onClick: () => this.close(false),
  };

  constructor() {
    effect(() => {
      const files = this.files();
      if (files) {
        this.partnerIds.set([]);
        this.title.set(files.length === 1 ? files[0].name : `${files.length} Elemente`);
        this.message.set('');
        this.expiresAt.set(new Date(Date.now() + 30 * day));
        void this.loadTargets();
      }
    });
  }

  partnerLabel = (target: ShareTarget | null): string => (target ? `${target.name} (${target.bpn})` : '');

  close(created: boolean): void {
    this.closed.emit(created);
  }

  private async loadTargets(): Promise<void> {
    try {
      this.targets.set(await this.api.shareTargets());
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  private async share(): Promise<void> {
    const files = this.files();
    if (!files || this.saving()) {
      return;
    }
    if (this.partnerIds().length === 0) {
      notify('Bitte mindestens einen Partner auswählen.', 'warning', 3000);
      return;
    }
    // Bis zum Ende des gewählten Tages gültig.
    const expires = new Date(this.expiresAt());
    expires.setHours(23, 59, 0, 0);
    this.saving.set(true);
    try {
      const share = await this.api.createShare({
        fileIds: files.filter((f) => !f.isDirectory).map((f) => f.id),
        folderIds: files.filter((f) => f.isDirectory).map((f) => f.id),
        partnerIds: this.partnerIds(),
        title: this.title().trim() || undefined,
        message: this.message().trim() || undefined,
        expiresAt: expires.toISOString(),
      });
      const names = share.deliveries.map((d) => d.partnerName).join(', ');
      notify(
        share.approval === 'Pending'
          ? `„${share.title}“ wartet auf Prüfung durch eine zweite Person.`
          : `„${share.title}“ wird für ${names} bereitgestellt.`,
        'success',
        4000,
      );
      this.close(true);
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    } finally {
      this.saving.set(false);
    }
  }
}
