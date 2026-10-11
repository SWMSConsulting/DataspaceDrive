import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type PrincipalType = 'User' | 'Role' | 'Group';
export type TeamPermission = 'Read' | 'Write';

/** Berechtigung auf einen Team-Bereich; bei Nutzern ist principalKey die Drive-Nutzer-ID. */
export interface AclEntry {
  principalType: PrincipalType;
  principalKey: string;
  permission: TeamPermission;
  displayName: string;
}

export interface TeamSpace {
  id: string;
  name: string;
  rootFolderId: string;
  createdAt: string;
  acl: AclEntry[];
  fileCount: number;
  storedBytes: number;
}

export interface KnownUser {
  id: string;
  displayName: string;
  email?: string;
  lastLoginAt: string;
  isActive: boolean;
}

/** Verwaltungs-API (Rolle drive-admin). */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
  private readonly http = inject(HttpClient);

  teamSpaces(): Promise<TeamSpace[]> {
    return firstValueFrom(this.http.get<TeamSpace[]>('/api/admin/spaces'));
  }

  createTeamSpace(name: string, acl: AclEntry[]): Promise<TeamSpace> {
    return firstValueFrom(this.http.post<TeamSpace>('/api/admin/spaces', { name, acl: acl.map(toRequest) }));
  }

  updateTeamSpace(id: string, name: string, acl: AclEntry[]): Promise<TeamSpace> {
    return firstValueFrom(this.http.put<TeamSpace>(`/api/admin/spaces/${id}`, { name, acl: acl.map(toRequest) }));
  }

  deleteTeamSpace(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/admin/spaces/${id}`));
  }

  knownUsers(): Promise<KnownUser[]> {
    return firstValueFrom(this.http.get<KnownUser[]>('/api/admin/users'));
  }
}

function toRequest(entry: AclEntry): Omit<AclEntry, 'displayName'> {
  return { principalType: entry.principalType, principalKey: entry.principalKey, permission: entry.permission };
}
