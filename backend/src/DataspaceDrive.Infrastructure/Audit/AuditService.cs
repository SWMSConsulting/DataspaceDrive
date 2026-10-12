using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataspaceDrive.Infrastructure.Audit;

public sealed class AuditService(DriveDbContext db) : IAuditService
{
    public async Task<IReadOnlyList<AuditEntryInfo>> RecentAsync(DrivePrincipal admin, int take, CancellationToken ct)
    {
        if (!admin.IsAdmin)
            throw new AccessDeniedException("Das Protokoll sehen nur Administratoren.");
        return await (from e in db.AuditEvents
                      join u in db.Users on e.ActorUserId equals u.Id into actors
                      from u in actors.DefaultIfEmpty()
                      orderby e.Id descending
                      select new AuditEntryInfo(e.Id, e.OccurredAt, u == null ? null : u.DisplayName, e.Action, e.TargetType, e.TargetId, e.Details))
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync(ct);
    }
}
