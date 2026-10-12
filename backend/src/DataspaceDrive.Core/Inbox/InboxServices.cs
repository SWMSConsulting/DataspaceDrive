using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Files;

namespace DataspaceDrive.Core.Inbox;

public sealed record IncomingItemInfo(string ItemKey, string Name, long Size, string Sha256, string ContentType, long ReceivedBytes,
    Guid? FileEntryId, ScanStatus? ScanStatus, string? RelativePath);

public sealed record IncomingOfferInfo(
    Guid Id,
    Guid PartnerId,
    string PartnerName,
    string ProviderBpn,
    string Title,
    string? Message,
    string? SenderOrganization,
    DateTime? ExpiresAt,
    long TotalSize,
    IncomingOfferState State,
    DateTime FirstSeenAt,
    Guid? TargetFolderId,
    string? TargetPath,
    string? DecidedByName,
    DateTime? DecidedAt,
    string? Error,
    DateTime? CompletedAt,
    IReadOnlyList<IncomingItemInfo> Items);

/// <summary>Eingang: Zustellungen der Partner an dieses Unternehmen. Annehmen und Ablehnen erfordert die Rolle drive-inbox.</summary>
public interface IInboxService
{
    Task<IReadOnlyList<IncomingOfferInfo>> ListAsync(DrivePrincipal principal, CancellationToken ct);

    /// <summary>Nimmt das Angebot an; die Dateien landen nach Prüfung im Zielordner (Schreibrecht nötig).</summary>
    Task<IncomingOfferInfo> AcceptAsync(DrivePrincipal principal, Guid offerId, Guid targetFolderId, CancellationToken ct);

    Task<IncomingOfferInfo> DeclineAsync(DrivePrincipal principal, Guid offerId, CancellationToken ct);

    /// <summary>Fehlgeschlagene Übernahme erneut versuchen.</summary>
    Task<IncomingOfferInfo> RetryAsync(DrivePrincipal principal, Guid offerId, CancellationToken ct);

    /// <summary>Kataloge der Partner sofort abfragen statt auf den nächsten Durchlauf zu warten.</summary>
    Task RequestRefreshAsync(DrivePrincipal principal, CancellationToken ct);
}
