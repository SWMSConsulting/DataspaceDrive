import { Component, computed, inject, signal } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxCheckBoxModule } from 'devextreme-angular/ui/check-box';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import { DxPopupModule } from 'devextreme-angular/ui/popup';
import { DxTextBoxModule } from 'devextreme-angular/ui/text-box';
import { confirm } from 'devextreme/ui/dialog';
import notify from 'devextreme/ui/notify';

import { AdminApiService, PartnerInfo, PartnerRequest } from '../../core/admin-api.service';
import { errorText } from '../../core/drive-api.service';

const emptyDraft: PartnerRequest = { bpn: '', name: '', dspAddress: '', sharingEnabled: true, receivingEnabled: true, blocked: false };

@Component({
  selector: 'app-partners',
  imports: [DxDataGridModule, DxButtonModule, DxCheckBoxModule, DxPopupModule, DxTextBoxModule],
  templateUrl: './partners.html',
  styleUrl: './partners.scss',
})
export class Partners {
  private readonly api = inject(AdminApiService);

  readonly partners = signal<PartnerInfo[]>([]);
  readonly editorVisible = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly draft = signal<PartnerRequest>({ ...emptyDraft });
  readonly editorTitle = computed(() => (this.editingId() ? 'Partner bearbeiten' : 'Partner anlegen'));

  readonly saveButtonOptions = {
    text: 'Speichern',
    type: 'default',
    stylingMode: 'contained',
    onClick: () => this.save(),
  };

  readonly cancelButtonOptions = {
    text: 'Abbrechen',
    stylingMode: 'outlined',
    onClick: () => this.editorVisible.set(false),
  };

  constructor() {
    void this.reload();
  }

  async reload(): Promise<void> {
    try {
      this.partners.set(await this.api.partners());
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  openCreate(): void {
    this.editingId.set(null);
    this.draft.set({ ...emptyDraft });
    this.editorVisible.set(true);
  }

  openEdit(partner: PartnerInfo): void {
    this.editingId.set(partner.id);
    this.draft.set({
      bpn: partner.bpn,
      name: partner.name,
      dspAddress: partner.dspAddress,
      sharingEnabled: partner.sharingEnabled,
      receivingEnabled: partner.receivingEnabled,
      blocked: partner.blocked,
    });
    this.editorVisible.set(true);
  }

  update<K extends keyof PartnerRequest>(key: K, value: PartnerRequest[K]): void {
    this.draft.update((d) => ({ ...d, [key]: value }));
  }

  async resolve(partner: PartnerInfo): Promise<void> {
    try {
      const resolved = await this.api.resolvePartner(partner.id);
      if (resolved.resolveError) {
        notify(`${resolved.name}: ${resolved.resolveError}`, 'error', 6000);
      } else {
        notify(`${resolved.name} ist erreichbar (${resolved.did}).`, 'success', 3000);
      }
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }

  async remove(partner: PartnerInfo): Promise<void> {
    if (!(await confirm(`Partner ${partner.name} löschen? Nach erfolgtem Austausch geht nur noch Sperren.`, 'Partner löschen'))) {
      return;
    }
    try {
      await this.api.deletePartner(partner.id);
      notify(`${partner.name} wurde gelöscht.`, 'success', 3000);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }

  formatStatus = (row: PartnerInfo): string => {
    if (row.blocked) {
      return 'gesperrt';
    }
    if (row.resolveError) {
      return 'nicht erreichbar';
    }
    return row.did ? 'aufgelöst' : 'offen';
  };
  formatFlags = (row: PartnerInfo): string =>
    [row.sharingEnabled ? 'teilen' : null, row.receivingEnabled ? 'empfangen' : null].filter(Boolean).join(', ') || '–';
  formatError = (row: PartnerInfo): string => row.resolveError ?? row.lastPollError ?? '';

  private async save(): Promise<void> {
    const draft = this.draft();
    try {
      const id = this.editingId();
      const saved = id ? await this.api.updatePartner(id, draft) : await this.api.createPartner(draft);
      if (saved.resolveError) {
        notify(`Gespeichert, aber nicht aufgelöst: ${saved.resolveError}`, 'warning', 6000);
      } else {
        notify(`${saved.name} wurde gespeichert (${saved.did}).`, 'success', 3000);
      }
      this.editorVisible.set(false);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }
}
