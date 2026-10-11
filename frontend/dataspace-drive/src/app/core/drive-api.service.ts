import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ScanStatus = 'Pending' | 'Clean' | 'Infected' | 'NotScannable' | 'Error' | 'NotScanned';

export interface SpaceInfo {
  id: string;
  name: string;
  kind: 'Personal' | 'Team';
  rootFolderId: string;
  permission: 'None' | 'Read' | 'Write' | 'Manage';
}

export interface DriveItem {
  id: string;
  isDirectory: boolean;
  name: string;
  spaceId: string;
  parentId: string | null;
  size: number;
  modifiedAt: string;
  scanStatus: ScanStatus | null;
  versionNo: number | null;
  contentType: string | null;
}

export interface FolderListing {
  folderId: string;
  spaceId: string;
  permission: SpaceInfo['permission'];
  path: { id: string; name: string }[];
  items: DriveItem[];
}

export interface TrashItem {
  id: string;
  isDirectory: boolean;
  name: string;
  spaceId: string;
  originalPath: string;
  deletedAt: string;
}

export interface UploadTicket {
  uploadId: string;
  chunkSize: number;
}

/** Zugriff auf die Datei-API des Backends. Ändernde Requests tragen automatisch das XSRF-Token. */
@Injectable({ providedIn: 'root' })
export class DriveApiService {
  private readonly http = inject(HttpClient);

  spaces(): Promise<SpaceInfo[]> {
    return firstValueFrom(this.http.get<SpaceInfo[]>('/api/spaces'));
  }

  children(folderId: string): Promise<FolderListing> {
    return firstValueFrom(this.http.get<FolderListing>(`/api/folders/${folderId}/children`));
  }

  createFolder(parentId: string, name: string): Promise<DriveItem> {
    return firstValueFrom(this.http.post<DriveItem>('/api/folders', { parentId, name }));
  }

  rename(itemId: string, name: string): Promise<DriveItem> {
    return firstValueFrom(this.http.patch<DriveItem>(`/api/items/${itemId}`, { name }));
  }

  move(itemId: string, targetFolderId: string): Promise<DriveItem> {
    return firstValueFrom(this.http.post<DriveItem>(`/api/items/${itemId}/move`, { targetFolderId }));
  }

  delete(itemId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/items/${itemId}`));
  }

  startUpload(folderId: string, fileName: string, size: number): Promise<UploadTicket> {
    return firstValueFrom(this.http.post<UploadTicket>('/api/uploads', { folderId, fileName, size }));
  }

  uploadChunk(uploadId: string, index: number, chunk: Blob): Promise<unknown> {
    return firstValueFrom(
      this.http.put(`/api/uploads/${uploadId}/chunks/${index}`, chunk, {
        headers: { 'Content-Type': 'application/octet-stream' },
      }),
    );
  }

  completeUpload(uploadId: string, contentType: string): Promise<DriveItem> {
    return firstValueFrom(this.http.post<DriveItem>(`/api/uploads/${uploadId}/complete`, { contentType }));
  }

  abortUpload(uploadId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/uploads/${uploadId}`));
  }

  trash(): Promise<TrashItem[]> {
    return firstValueFrom(this.http.get<TrashItem[]>('/api/trash'));
  }

  restore(itemId: string): Promise<DriveItem> {
    return firstValueFrom(this.http.post<DriveItem>(`/api/trash/${itemId}/restore`, {}));
  }

  purge(itemId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/trash/${itemId}`));
  }

  search(query: string): Promise<DriveItem[]> {
    return firstValueFrom(this.http.get<DriveItem[]>('/api/search', { params: { q: query } }));
  }

  contentUrl(fileId: string): string {
    return `/api/files/${fileId}/content`;
  }
}

/** Meldung des Servers (ProblemDetails.title), sonst ein allgemeiner Text. */
export function errorText(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const title = (error.error as { title?: string } | null)?.title;
    if (title) {
      return title;
    }
    if (error.status === 0) {
      return 'Der Server ist nicht erreichbar.';
    }
    return `Fehler ${error.status}`;
  }
  return 'Unbekannter Fehler';
}
