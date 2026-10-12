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
  externalSharingAllowed: boolean;
}

export interface PartnerInfo {
  id: string;
  bpn: string;
  name: string;
  dspAddress: string;
  did: string | null;
  protocol: string | null;
  resolvedAt: string | null;
  resolveError: string | null;
  sharingEnabled: boolean;
  receivingEnabled: boolean;
  blocked: boolean;
  lastPolledAt: string | null;
  lastPollError: string | null;
}

export interface PartnerRequest {
  bpn: string;
  name: string;
  dspAddress: string;
  sharingEnabled: boolean;
  receivingEnabled: boolean;
  blocked: boolean;
}

export interface AuditEntry {
  id: number;
  occurredAt: string;
  actorName: string | null;
  action: string;
  targetType: string | null;
  targetId: string | null;
  details: string | null;
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

  createTeamSpace(name: string, acl: AclEntry[], externalSharingAllowed: boolean): Promise<TeamSpace> {
    return firstValueFrom(this.http.post<TeamSpace>('/api/admin/spaces', { name, acl: acl.map(toRequest), externalSharingAllowed }));
  }

  updateTeamSpace(id: string, name: string, acl: AclEntry[], externalSharingAllowed: boolean): Promise<TeamSpace> {
    return firstValueFrom(this.http.put<TeamSpace>(`/api/admin/spaces/${id}`, { name, acl: acl.map(toRequest), externalSharingAllowed }));
  }

  deleteTeamSpace(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/admin/spaces/${id}`));
  }

  knownUsers(): Promise<KnownUser[]> {
    return firstValueFrom(this.http.get<KnownUser[]>('/api/admin/users'));
  }

  partners(): Promise<PartnerInfo[]> {
    return firstValueFrom(this.http.get<PartnerInfo[]>('/api/admin/partners'));
  }

  createPartner(request: PartnerRequest): Promise<PartnerInfo> {
    return firstValueFrom(this.http.post<PartnerInfo>('/api/admin/partners', request));
  }

  updatePartner(id: string, request: PartnerRequest): Promise<PartnerInfo> {
    return firstValueFrom(this.http.put<PartnerInfo>(`/api/admin/partners/${id}`, request));
  }

  deletePartner(id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`/api/admin/partners/${id}`));
  }

  resolvePartner(id: string): Promise<PartnerInfo> {
    return firstValueFrom(this.http.post<PartnerInfo>(`/api/admin/partners/${id}/resolve`, {}));
  }

  audit(take = 500): Promise<AuditEntry[]> {
    return firstValueFrom(this.http.get<AuditEntry[]>('/api/admin/audit', { params: { take } }));
  }
}

function toRequest(entry: AclEntry): Omit<AclEntry, 'displayName'> {
  return { principalType: entry.principalType, principalKey: entry.principalKey, permission: entry.permission };
}
