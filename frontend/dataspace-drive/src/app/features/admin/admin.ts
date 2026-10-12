import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxCheckBoxModule } from 'devextreme-angular/ui/check-box';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import { DxPopupModule } from 'devextreme-angular/ui/popup';
import { DxSelectBoxModule } from 'devextreme-angular/ui/select-box';
import { DxTextBoxModule } from 'devextreme-angular/ui/text-box';
import { CustomItemCreatingEvent } from 'devextreme/ui/select_box';
import { confirm } from 'devextreme/ui/dialog';
import notify from 'devextreme/ui/notify';

import {
  AclEntry,
  AdminApiService,
  EdcFinding,
  EdcStatus,
  KnownUser,
  PrincipalType,
  TeamPermission,
  TeamSpace,
} from '../../core/admin-api.service';
import { errorText } from '../../core/drive-api.service';
import { formatBytes } from '../../shared/format';

const typeLabels: Record<PrincipalType, string> = { Role: 'Rolle', Group: 'Gruppe', User: 'Nutzer' };
const permissionLabels: Record<TeamPermission, string> = { Read: 'Lesen', Write: 'Schreiben' };

@Component({
  selector: 'app-admin',
  imports: [DatePipe, DxDataGridModule, DxButtonModule, DxCheckBoxModule, DxPopupModule, DxSelectBoxModule, DxTextBoxModule],
  templateUrl: './admin.html',
  styleUrl: './admin.scss',
})
export class Admin {
  private readonly api = inject(AdminApiService);

  readonly spaces = signal<TeamSpace[]>([]);
  readonly edc = signal<EdcStatus | null>(null);
  readonly requireApproval = signal(false);
  readonly contractProfile = signal<string | null>(null);
  readonly checking = signal(false);
  readonly users = signal<KnownUser[]>([]);

  readonly editorVisible = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly draftName = signal('');
  readonly draftAcl = signal<AclEntry[]>([]);
  readonly draftExternal = signal(false);
  readonly saving = signal(false);
  readonly editorTitle = computed(() => (this.editingId() ? 'Team-Bereich bearbeiten' : 'Team-Bereich anlegen'));

  readonly newType = signal<PrincipalType>('Role');
  readonly newKey = signal<string | null>(null);
  readonly newPermission = signal<TeamPermission>('Write');

  readonly typeOptions = (Object.keys(typeLabels) as PrincipalType[]).map((value) => ({ value, text: typeLabels[value] }));
  readonly permissionOptions = (Object.keys(permissionLabels) as TeamPermission[]).map((value) => ({ value, text: permissionLabels[value] }));
  /** Rollen dieser Anwendung; weitere Rollen und Gruppen aus dem Identity Provider lassen sich frei eintragen. */
  readonly roleSuggestions = ['drive-user', 'drive-external-share', 'drive-inbox', 'drive-admin'];

  readonly plannedItems = [
    'Policy-Profile (CX-Nutzungsbedingungen)',
    'Ausstehende EDC-Operationen (Outbox) einsehen und anstoßen',
    'Quarantäne für verdächtige Dateien',
  ];

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
      const [spaces, users, edc, sharing] = await Promise.all([
        this.api.teamSpaces(),
        this.api.knownUsers(),
        this.api.edcStatus(),
        this.api.sharingSettings(),
      ]);
      this.spaces.set(spaces);
      this.users.set(users);
      this.edc.set(edc);
      this.requireApproval.set(sharing.requireApproval);
      this.contractProfile.set(sharing.contractProfile);
    } catch (error) {
      notify(errorText(error), 'error', 4000);
    }
  }

  async setRequireApproval(value: boolean): Promise<void> {
    if (value === this.requireApproval()) {
      return;
    }
    try {
      const saved = await this.api.updateSharingSettings(value);
      this.requireApproval.set(saved.requireApproval);
      notify(value ? 'Vier-Augen-Prinzip eingeschaltet.' : 'Vier-Augen-Prinzip ausgeschaltet.', 'success', 3000);
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }

  async checkEdc(): Promise<void> {
    this.checking.set(true);
    try {
      const status = await this.api.checkEdc();
      this.edc.set(status);
      const critical = status.findings.filter((f) => f.severity === 'Critical').length;
      notify(
        status.reachable ? `Abgleich fertig: ${status.findings.length} Befund(e), davon ${critical} kritisch.` : `Connector nicht erreichbar: ${status.error}`,
        critical > 0 || !status.reachable ? 'warning' : 'success',
        4000,
      );
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    } finally {
      this.checking.set(false);
    }
  }

  formatSeverity = (row: EdcFinding): string => ({ Critical: 'kritisch', Warning: 'Warnung', Info: 'Hinweis' })[row.severity];

  openCreate(): void {
    this.openEditor(null, '', [], false);
  }

  openEdit(space: TeamSpace): void {
    this.openEditor(space.id, space.name, space.acl, space.externalSharingAllowed);
  }

  onTypeChanged(type: PrincipalType): void {
    this.newType.set(type);
    this.newKey.set(null);
  }

  onCustomRole(e: CustomItemCreatingEvent): void {
    e.customItem = e.text?.trim();
  }

  addEntry(): void {
    const key = (this.newKey() ?? '').trim();
    const type = this.newType();
    if (!key) {
      notify('Bitte einen Nutzer, eine Rolle oder eine Gruppe angeben.', 'warning', 3000);
      return;
    }
    if (this.draftAcl().some((e) => e.principalType === type && e.principalKey === key)) {
      notify('Dieser Eintrag ist schon vorhanden.', 'warning', 3000);
      return;
    }
    const displayName = type === 'User' ? this.userLabel(this.users().find((u) => u.id === key)) : key;
    this.draftAcl.update((acl) => [...acl, { principalType: type, principalKey: key, permission: this.newPermission(), displayName }]);
    this.newKey.set(null);
  }

  removeEntry(entry: AclEntry): void {
    this.draftAcl.update((acl) => acl.filter((e) => e !== entry));
  }

  setPermission(entry: AclEntry, permission: TeamPermission): void {
    this.draftAcl.update((acl) => acl.map((e) => (e === entry ? { ...e, permission } : e)));
  }

  async save(): Promise<void> {
    const name = this.draftName().trim();
    if (!name) {
      notify('Bitte einen Namen angeben.', 'warning', 3000);
      return;
    }
    if (this.saving()) {
      return;
    }
    this.saving.set(true);
    try {
      const id = this.editingId();
      const saved = id
        ? await this.api.updateTeamSpace(id, name, this.draftAcl(), this.draftExternal())
        : await this.api.createTeamSpace(name, this.draftAcl(), this.draftExternal());
      notify(`„${saved.name}“ wurde gespeichert.`, 'success', 3000);
      this.editorVisible.set(false);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    } finally {
      this.saving.set(false);
    }
  }

  async remove(space: TeamSpace): Promise<void> {
    const ok = await confirm(`Team-Bereich „${space.name}“ löschen? Das geht nur, wenn er leer ist (auch im Papierkorb).`, 'Team-Bereich löschen');
    if (!ok) {
      return;
    }
    try {
      await this.api.deleteTeamSpace(space.id);
      notify(`„${space.name}“ wurde gelöscht.`, 'success', 3000);
      await this.reload();
    } catch (error) {
      notify(errorText(error), 'error', 5000);
    }
  }

  userLabel = (user: KnownUser | null | undefined): string =>
    user ? (user.email ? `${user.displayName} (${user.email})` : user.displayName) : '';

  formatAcl = (row: TeamSpace): string =>
    row.acl.length === 0
      ? 'niemand'
      : row.acl.map((e) => `${typeLabels[e.principalType]} ${e.displayName}: ${permissionLabels[e.permission]}`).join(' · ');

  formatSize = (row: TeamSpace): string => formatBytes(row.storedBytes);
  formatExternal = (row: TeamSpace): string => (row.externalSharingAllowed ? 'ja' : 'nein');
  formatType = (row: AclEntry): string => typeLabels[row.principalType];

  private openEditor(id: string | null, name: string, acl: AclEntry[], external: boolean): void {
    this.editingId.set(id);
    this.draftName.set(name);
    this.draftExternal.set(external);
    this.draftAcl.set(acl.map((e) => ({ ...e })));
    this.newType.set('Role');
    this.newKey.set(null);
    this.newPermission.set('Write');
    this.editorVisible.set(true);
  }
}
