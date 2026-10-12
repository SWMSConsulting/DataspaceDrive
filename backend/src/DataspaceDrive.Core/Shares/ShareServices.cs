using DataspaceDrive.Core.Access;

namespace DataspaceDrive.Core.Shares;

/// <param name="FolderIds">Ordner werden mit allen Unterordnern und Dateien geteilt; der Empfänger erhält dieselbe Struktur.</param>
public sealed record CreateShareRequest(
    IReadOnlyList<Guid>? FileIds,
    IReadOnlyList<Guid> PartnerIds,
    string? Title,
    string? Message,
    DateTime? ExpiresAt,
    IReadOnlyList<Guid>? FolderIds = null);

public sealed record ShareItemInfo(string ItemKey, string Name, long Size, string Sha256, Guid FileEntryId, string? RelativePath);

public sealed record DeliveryInfo(
    Guid Id,
    Guid PartnerId,
    string PartnerName,
    string RecipientBpn,
    DeliveryState State,
    DateTime ExpiresAt,
    DateTime? ActivatedAt,
    DateTime? EndedAt,
    int RetrievalCount,
    DateTime? LastRetrievalAt,
    string? LastError);

/// <param name="HasNewerVersions">Mindestens eine geteilte Datei hat inzwischen eine neuere Version (Anlass zum Aktualisieren).</param>
public sealed record ShareSummary(
    Guid Id,
    string Title,
    string? Message,
    Guid CreatedBy,
    string CreatedByName,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    IReadOnlyList<ShareItemInfo> Items,
    IReadOnlyList<DeliveryInfo> Deliveries,
    Guid LineageId,
    int Revision,
    bool HasNewerVersions);

/// <summary>Aktualisieren: Nachricht und Ablauf optional, sonst wie bisher.</summary>
public sealed record UpdateShareRequest(string? Message, DateTime? ExpiresAt);

public sealed record RetrievalInfo(DateTime OccurredAt, string Decision, string? ConsumerBpn, string? AgreementId, string? ItemKey,
    long? Offset, long? Length);

/// <summary>Freigaben an Partnerunternehmen; nur Nutzer mit der Rolle drive-external-share dürfen teilen.</summary>
public interface IShareService
{
    Task<ShareSummary> CreateAsync(DrivePrincipal principal, CreateShareRequest request, CancellationToken ct);

    /// <summary>Eigene Freigaben; Administratoren mit <paramref name="all"/> alle.</summary>
    Task<IReadOnlyList<ShareSummary>> ListAsync(DrivePrincipal principal, bool all, CancellationToken ct);

    Task<IReadOnlyList<RetrievalInfo>> RetrievalsAsync(DrivePrincipal principal, Guid deliveryId, CancellationToken ct);

    Task<DeliveryInfo> RevokeDeliveryAsync(DrivePrincipal principal, Guid deliveryId, CancellationToken ct);

    Task<ShareSummary> RevokeShareAsync(DrivePrincipal principal, Guid shareId, CancellationToken ct);

    /// <summary>Fehlgeschlagene Anlage erneut versuchen.</summary>
    Task<DeliveryInfo> RetryDeliveryAsync(DrivePrincipal principal, Guid deliveryId, CancellationToken ct);

    /// <summary>
    /// Teilt den aktuellen Stand der ursprünglichen Auswahl als neue Revision an alle noch belieferten Partner und löst die
    /// bisherige Freigabe ab.
    /// </summary>
    Task<ShareSummary> UpdateAsync(DrivePrincipal principal, Guid shareId, UpdateShareRequest request, CancellationToken ct);
}
