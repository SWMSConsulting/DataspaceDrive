import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ScanStatus } from './drive-api.service';

export type DeliveryState =
  | 'Requested'
  | 'Provisioning'
  | 'ProvisioningFailed'
  | 'Rejected'
  | 'Active'
  | 'Revoking'
  | 'Revoked'
  | 'Expiring'
  | 'Expired';

export type IncomingOfferState =
  | 'New'
  | 'Accepted'
  | 'Negotiating'
  | 'AwaitingTransfer'
  | 'Downloading'
  | 'Verifying'
  | 'Scanning'
  | 'Completed'
  | 'Declined'
  | 'Withdrawn'
  | 'Expired'
  | 'Failed'
  | 'Quarantined';

export interface ShareTarget {
  id: string;
  bpn: string;
  name: string;
}

export interface DeliveryInfo {
  id: string;
  partnerId: string;
  partnerName: string;
  recipientBpn: string;
  state: DeliveryState;
  expiresAt: string;
  activatedAt: string | null;
  endedAt: string | null;
  retrievalCount: number;
  lastRetrievalAt: string | null;
  lastError: string | null;
}

export interface ShareSummary {
  id: string;
  title: string;
  message: string | null;
  createdBy: string;
  createdByName: string;
  createdAt: string;
  expiresAt: string;
  items: { itemKey: string; name: string; size: number; sha256: string; fileEntryId: string }[];
  deliveries: DeliveryInfo[];
}

export interface RetrievalInfo {
  occurredAt: string;
  decision: string;
  consumerBpn: string | null;
  agreementId: string | null;
  itemKey: string | null;
  offset: number | null;
  length: number | null;
}

export interface IncomingItemInfo {
  itemKey: string;
  name: string;
  size: number;
  sha256: string;
  contentType: string;
  receivedBytes: number;
  fileEntryId: string | null;
  scanStatus: ScanStatus | null;
}

export interface IncomingOfferInfo {
  id: string;
  partnerId: string;
  partnerName: string;
  providerBpn: string;
  title: string;
  message: string | null;
  senderOrganization: string | null;
  expiresAt: string | null;
  totalSize: number;
  state: IncomingOfferState;
  firstSeenAt: string;
  targetFolderId: string | null;
  targetPath: string | null;
  decidedByName: string | null;
  decidedAt: string | null;
  error: string | null;
  completedAt: string | null;
  items: IncomingItemInfo[];
}

export interface CreateShareRequest {
  fileIds: string[];
  partnerIds: string[];
  title?: string;
  message?: string;
  expiresAt?: string;
}

/** Freigaben an Partner und Eingang. */
@Injectable({ providedIn: 'root' })
export class SharingApiService {
  private readonly http = inject(HttpClient);

  shareTargets(): Promise<ShareTarget[]> {
    return firstValueFrom(this.http.get<ShareTarget[]>('/api/partners/targets'));
  }

  createShare(request: CreateShareRequest): Promise<ShareSummary> {
    return firstValueFrom(this.http.post<ShareSummary>('/api/shares', request));
  }

  shares(all = false): Promise<ShareSummary[]> {
    return firstValueFrom(this.http.get<ShareSummary[]>('/api/shares', { params: all ? { all: 'true' } : {} }));
  }

  revokeShare(shareId: string): Promise<ShareSummary> {
    return firstValueFrom(this.http.post<ShareSummary>(`/api/shares/${shareId}/revoke`, {}));
  }

  revokeDelivery(deliveryId: string): Promise<DeliveryInfo> {
    return firstValueFrom(this.http.post<DeliveryInfo>(`/api/deliveries/${deliveryId}/revoke`, {}));
  }

  retryDelivery(deliveryId: string): Promise<DeliveryInfo> {
    return firstValueFrom(this.http.post<DeliveryInfo>(`/api/deliveries/${deliveryId}/retry`, {}));
  }

  retrievals(deliveryId: string): Promise<RetrievalInfo[]> {
    return firstValueFrom(this.http.get<RetrievalInfo[]>(`/api/deliveries/${deliveryId}/retrievals`));
  }

  inbox(): Promise<IncomingOfferInfo[]> {
    return firstValueFrom(this.http.get<IncomingOfferInfo[]>('/api/inbox'));
  }

  refreshInbox(): Promise<unknown> {
    return firstValueFrom(this.http.post('/api/inbox/refresh', {}));
  }

  accept(offerId: string, folderId: string): Promise<IncomingOfferInfo> {
    return firstValueFrom(this.http.post<IncomingOfferInfo>(`/api/inbox/${offerId}/accept`, { folderId }));
  }

  decline(offerId: string): Promise<IncomingOfferInfo> {
    return firstValueFrom(this.http.post<IncomingOfferInfo>(`/api/inbox/${offerId}/decline`, {}));
  }

  retryOffer(offerId: string): Promise<IncomingOfferInfo> {
    return firstValueFrom(this.http.post<IncomingOfferInfo>(`/api/inbox/${offerId}/retry`, {}));
  }
}

const deliveryLabels: Record<DeliveryState, string> = {
  Requested: 'angefordert',
  Provisioning: 'wird angelegt',
  ProvisioningFailed: 'Anlage fehlgeschlagen',
  Rejected: 'abgelehnt',
  Active: 'aktiv',
  Revoking: 'wird widerrufen',
  Revoked: 'widerrufen',
  Expiring: 'läuft ab',
  Expired: 'abgelaufen',
};

const offerLabels: Record<IncomingOfferState, string> = {
  New: 'neu',
  Accepted: 'angenommen',
  Negotiating: 'Verhandlung',
  AwaitingTransfer: 'Transfer wird vorbereitet',
  Downloading: 'wird übertragen',
  Verifying: 'wird geprüft',
  Scanning: 'Virenscan',
  Completed: 'abgelegt',
  Declined: 'abgelehnt',
  Withdrawn: 'zurückgezogen',
  Expired: 'abgelaufen',
  Failed: 'fehlgeschlagen',
  Quarantined: 'gesperrt (Virenscan)',
};

export function deliveryLabel(state: DeliveryState): string {
  return deliveryLabels[state] ?? state;
}

export function offerLabel(state: IncomingOfferState): string {
  return offerLabels[state] ?? state;
}

/** Zustände, in denen der Eingang noch arbeitet (für automatisches Neuladen). */
export function offerInProgress(state: IncomingOfferState): boolean {
  return ['Accepted', 'Negotiating', 'AwaitingTransfer', 'Downloading', 'Verifying', 'Scanning'].includes(state);
}

export function deliveryInProgress(state: DeliveryState): boolean {
  return ['Requested', 'Provisioning', 'Revoking', 'Expiring'].includes(state);
}
