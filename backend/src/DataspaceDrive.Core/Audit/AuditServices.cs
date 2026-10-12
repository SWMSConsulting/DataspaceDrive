using DataspaceDrive.Core.Access;

namespace DataspaceDrive.Core.Audit;

public sealed record AuditEntryInfo(long Id, DateTime OccurredAt, string? ActorName, string Action, string? TargetType, string? TargetId,
    string? Details);

/// <summary>Lesender Zugriff auf das Audit-Protokoll; nur für Administratoren.</summary>
public interface IAuditService
{
    Task<IReadOnlyList<AuditEntryInfo>> RecentAsync(DrivePrincipal admin, int take, CancellationToken ct);
}
