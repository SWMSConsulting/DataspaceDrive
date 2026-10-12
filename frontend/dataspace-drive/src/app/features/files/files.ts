import { Component, computed, inject, signal, viewChild } from '@angular/core';
import { DxButtonModule } from 'devextreme-angular/ui/button';
import { DxDataGridModule } from 'devextreme-angular/ui/data-grid';
import { DxFileManagerComponent, DxFileManagerModule } from 'devextreme-angular/ui/file-manager';
import { DxTextBoxModule } from 'devextreme-angular/ui/text-box';
import CustomFileSystemProvider from 'devextreme/file_management/custom_provider';
import FileSystemError from 'devextreme/file_management/error';
import FileSystemItem from 'devextreme/file_management/file_system_item';
import UploadInfo from 'devextreme/file_management/upload_info';
import notify from 'devextreme/ui/notify';
import {
  ContextMenuItemClickEvent,
  CurrentDirectoryChangedEvent,
  dxFileManagerContextMenuItem,
  dxFileManagerToolbarItem,
  FileManagerPredefinedContextMenuItem,
  FileManagerPredefinedToolbarItem,
  SelectionChangedEvent,
} from 'devextreme/ui/file_manager';

import { AuthService } from '../../core/auth.service';
import { ConfigService } from '../../core/config.service';
import { DriveApiService, DriveItem, SpaceInfo, errorText } from '../../core/drive-api.service';
import { downloadFile, formatBytes, scanLabel } from '../../shared/format';
import { ShareDialog, ShareFile } from './share-dialog/share-dialog';

/** Ordner bzw. Bereich im FileManager; Bereiche zeigen auf ihren Wurzelordner. */
interface DirectoryData {
  key: string;
  name: string;
  isDirectory: true;
  folderId: string;
  isSpace: boolean;
  dateModified?: Date;
}

interface FileData {
  key: string;
  name: string;
  isDirectory: false;
  id: string;
  size: number;
  dateModified: Date;
  scanLabel: string;
}

const otherError = 32767;
const uploadChunkSize = 8 * 1024 * 1024;
const defaultMaxUpload = 10 * 1024 * 1024 * 1024;

const writablePermissions = { create: true, copy: false, move: true, delete: true, rename: true, upload: true, download: true };
// In der Wurzel stehen nur die Bereiche; dort und in Bereichen mit Leserecht ist nichts änderbar.
const readOnlyPermissions = { create: false, copy: false, move: false, delete: false, rename: false, upload: false, download: true };

@Component({
  selector: 'app-files',
  imports: [DxFileManagerModule, DxTextBoxModule, DxButtonModule, DxDataGridModule, ShareDialog],
  templateUrl: './files.html',
  styleUrl: './files.scss',
})
export class Files {
  private readonly api = inject(DriveApiService);
  private readonly configService = inject(ConfigService);
  private readonly auth = inject(AuthService);
  private readonly fileManager = viewChild(DxFileManagerComponent);

  /** Recht je Ordner-Schlüssel, aus den geladenen Listen; der Server prüft trotzdem jede Aktion. */
  private readonly folderPermissions = new Map<string, SpaceInfo['permission']>();
  /** Darf aus dem Bereich dieses Ordners an Partner freigegeben werden? */
  private readonly folderSharable = new Map<string, boolean>();
  private readonly sharableHere = signal(false);
  private readonly itemsSelected = signal(false);

  readonly shareFiles = signal<ShareFile[] | null>(null);
  readonly canShare = computed(() => !!this.auth.me()?.canShareExternally && this.sharableHere());

  readonly toolbar = computed(() => {
    const items: (dxFileManagerToolbarItem | FileManagerPredefinedToolbarItem)[] = ['download', 'separator', 'move', 'rename', 'separator', 'delete'];
    if (this.canShare() && this.itemsSelected()) {
      items.push({
        name: 'share',
        widget: 'dxButton',
        location: 'before',
        options: { text: 'An Partner freigeben', icon: 'share', stylingMode: 'text', onClick: () => this.openShare() },
      });
    }
    items.push('clearSelection', { name: 'separator', location: 'after' }, 'refresh');
    return { fileSelectionItems: items };
  });
  readonly contextMenu = computed(() => {
    const items: (dxFileManagerContextMenuItem | FileManagerPredefinedContextMenuItem)[] = [
      'create', 'upload', 'rename', 'move', 'download', 'delete', 'refresh',
    ];
    if (this.canShare()) {
      items.push({ name: 'share', text: 'An Partner freigeben', icon: 'share', beginGroup: true });
    }
    return { items };
  });

  readonly searchTerm = signal('');
  readonly searchResults = signal<DriveItem[] | null>(null);
  readonly searchError = signal<string | null>(null);

  readonly permissions = signal(readOnlyPermissions);
  readonly upload = computed(() => ({
    chunkSize: uploadChunkSize,
    maxFileSize: this.configService.config()?.maxUploadBytes ?? defaultMaxUpload,
  }));
  readonly itemView = {
    mode: 'details' as const,
    details: {
      columns: [
        'thumbnail',
        'name',
        { dataField: 'scanLabel', caption: 'Prüfung', width: 130 },
        'dateModified',
        'size',
      ],
    },
  };

  readonly fileProvider = new CustomFileSystemProvider({
    getItems: (parent) => this.guard(parent, () => this.getItems(parent)),
    createDirectory: (parent, name) => this.guard(parent, () => this.api.createFolder(this.folderId(parent), name)),
    renameItem: (item, name) =>
      this.guard(item, () => (this.isSpace(item) ? this.fail(item, 'Bereiche können nicht umbenannt werden.') : this.api.rename(this.itemId(item), name))),
    deleteItem: (item) =>
      this.guard(item, () => (this.isSpace(item) ? this.fail(item, 'Bereiche können nicht gelöscht werden.') : this.api.delete(this.itemId(item)))),
    moveItem: (item, destination) =>
      this.guard(item, () =>
        this.isSpace(item) ? this.fail(item, 'Bereiche können nicht verschoben werden.') : this.api.move(this.itemId(item), this.folderId(destination)),
      ),
    uploadFileChunk: (file, uploadInfo, destination) => this.guard(destination, () => this.uploadChunk(file, uploadInfo, destination)),
    abortFileUpload: (_file, uploadInfo) => this.abortUpload(uploadInfo),
    downloadItems: (items) => items.filter((i) => !i.isDirectory).forEach((i) => downloadFile(this.api.contentUrl(this.itemId(i)))),
  });

  async search(): Promise<void> {
    const term = this.searchTerm().trim();
    this.searchError.set(null);
    if (term.length < 2) {
      this.searchResults.set(null);
      return;
    }
    try {
      this.searchResults.set(await this.api.search(term));
    } catch (error) {
      this.searchError.set(errorText(error));
    }
  }

  clearSearch(): void {
    this.searchTerm.set('');
    this.searchResults.set(null);
    this.searchError.set(null);
  }

  onCurrentDirectoryChanged(e: CurrentDirectoryChangedEvent): void {
    const directory = e.directory;
    const inSpace = !!directory && directory.pathKeys.length > 0;
    const permission = inSpace ? this.folderPermissions.get(this.folderId(directory)) : undefined;
    this.permissions.set(permission === 'Write' || permission === 'Manage' ? writablePermissions : readOnlyPermissions);
    this.sharableHere.set(inSpace && !!this.folderSharable.get(this.folderId(directory)));
  }

  onSelectionChanged(e: SelectionChangedEvent): void {
    const items = e.selectedItems ?? [];
    this.itemsSelected.set(items.length > 0 && !items.some((i) => this.isSpace(i)));
  }

  onMenuItemClick(e: ContextMenuItemClickEvent): void {
    if ((e.itemData as { name?: string } | undefined)?.name === 'share') {
      this.openShare();
    }
  }

  private openShare(): void {
    const selected = (this.fileManager()?.instance.getSelectedItems() ?? []).filter((i) => !this.isSpace(i));
    if (selected.length === 0) {
      notify('Bitte Dateien oder Ordner auswählen.', 'warning', 3000);
      return;
    }
    this.shareFiles.set(
      selected.map((i) => ({ id: i.isDirectory ? this.folderId(i) : this.itemId(i), name: i.name, isDirectory: i.isDirectory })),
    );
  }

  download(item: DriveItem): void {
    downloadFile(this.api.contentUrl(item.id));
  }

  formatSize = (row: DriveItem): string => (row.isDirectory ? '' : formatBytes(row.size));
  formatType = (row: DriveItem): string => (row.isDirectory ? 'Ordner' : 'Datei');
  formatScan = (row: DriveItem): string => scanLabel(row.scanStatus);

  private async getItems(parent: FileSystemItem): Promise<(DirectoryData | FileData)[]> {
    if (parent.pathKeys.length === 0) {
      const spaces = await this.api.spaces();
      spaces.forEach((s) => {
        this.folderPermissions.set(s.rootFolderId, s.permission);
        this.folderSharable.set(s.rootFolderId, s.externalSharingAllowed);
      });
      return spaces.map((s) => ({ key: s.rootFolderId, name: s.name, isDirectory: true, folderId: s.rootFolderId, isSpace: true }));
    }
    const listing = await this.api.children(this.folderId(parent));
    this.folderPermissions.set(listing.folderId, listing.permission);
    const sharable = this.folderSharable.get(listing.folderId) ?? false;
    listing.items
      .filter((item) => item.isDirectory)
      .forEach((item) => {
        this.folderPermissions.set(item.id, listing.permission);
        this.folderSharable.set(item.id, sharable);
      });
    return listing.items.map((item) =>
      item.isDirectory
        ? { key: item.id, name: item.name, isDirectory: true, folderId: item.id, isSpace: false, dateModified: new Date(item.modifiedAt) }
        : {
            key: item.id,
            name: item.name,
            isDirectory: false,
            id: item.id,
            size: item.size,
            dateModified: new Date(item.modifiedAt),
            scanLabel: scanLabel(item.scanStatus),
          },
    );
  }

  /** Erstes Teilstück startet den Upload, das letzte schließt ihn ab; die Upload-ID reist in customData mit. */
  private async uploadChunk(file: File, uploadInfo: UploadInfo, destination: FileSystemItem): Promise<void> {
    if (uploadInfo.chunkIndex === 0) {
      const ticket = await this.api.startUpload(this.folderId(destination), file.name, file.size);
      uploadInfo.customData.uploadId = ticket.uploadId;
    }
    const uploadId = uploadInfo.customData.uploadId as string;
    await this.api.uploadChunk(uploadId, uploadInfo.chunkIndex, uploadInfo.chunkBlob);
    if (uploadInfo.chunkIndex === Math.max(uploadInfo.chunkCount - 1, 0)) {
      await this.api.completeUpload(uploadId, file.type);
    }
  }

  private async abortUpload(uploadInfo: UploadInfo): Promise<void> {
    const uploadId = uploadInfo.customData?.uploadId as string | undefined;
    if (uploadId) {
      await this.api.abortUpload(uploadId).catch(() => undefined);
    }
  }

  private folderId(item: FileSystemItem): string {
    return (item.dataItem as DirectoryData).folderId;
  }

  private itemId(item: FileSystemItem): string {
    return (item.dataItem as DirectoryData | FileData).key;
  }

  private isSpace(item: FileSystemItem): boolean {
    return item.isDirectory && (item.dataItem as DirectoryData).isSpace;
  }

  private fail(item: FileSystemItem, message: string): never {
    throw new FileSystemError(otherError, item, message);
  }

  /** Server-Fehler als FileSystemError mit deutscher Meldung, damit der FileManager sie anzeigt. */
  private async guard<T>(item: FileSystemItem, action: () => Promise<T> | T): Promise<T> {
    try {
      return await action();
    } catch (error) {
      if (error instanceof FileSystemError) {
        throw error;
      }
      throw new FileSystemError(otherError, item, errorText(error));
    }
  }
}
