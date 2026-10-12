using DataspaceDrive.Core.Access;

namespace DataspaceDrive.Core.Partners;

public sealed record PartnerInfo(
    Guid Id,
    string Bpn,
    string Name,
    string DspAddress,
    string? Did,
    string? Protocol,
    DateTime? ResolvedAt,
    string? ResolveError,
    bool SharingEnabled,
    bool ReceivingEnabled,
    bool Blocked,
    DateTime? LastPolledAt,
    string? LastPollError,
    AutoAcceptMode AutoAccept,
    Guid? AutoAcceptFolderId,
    string? AutoAcceptFolderPath,
    string? AutoAcceptUserName);

public sealed record PartnerRequest(string Bpn, string Name, string DspAddress, bool SharingEnabled = true, bool ReceivingEnabled = true,
    bool Blocked = false, AutoAcceptMode AutoAccept = AutoAcceptMode.Off, Guid? AutoAcceptFolderId = null);

/// <summary>Partner, an den ein Nutzer freigeben kann (für den Teilen-Dialog).</summary>
public sealed record ShareTarget(Guid Id, string Bpn, string Name);

public interface IPartnerService
{
    Task<IReadOnlyList<PartnerInfo>> ListAsync(DrivePrincipal admin, CancellationToken ct);

    /// <summary>Legt den Partner an und löst sofort DID und Protokoll auf; ein Fehler dabei wird vermerkt, nicht geworfen.</summary>
    Task<PartnerInfo> CreateAsync(DrivePrincipal admin, PartnerRequest request, CancellationToken ct);

    Task<PartnerInfo> UpdateAsync(DrivePrincipal admin, Guid partnerId, PartnerRequest request, CancellationToken ct);

    /// <summary>Nur ohne Zustellungen und Eingänge; sonst sperren statt löschen.</summary>
    Task DeleteAsync(DrivePrincipal admin, Guid partnerId, CancellationToken ct);

    Task<PartnerInfo> ResolveAsync(DrivePrincipal admin, Guid partnerId, CancellationToken ct);

    Task<IReadOnlyList<ShareTarget>> ShareTargetsAsync(DrivePrincipal principal, CancellationToken ct);
}
