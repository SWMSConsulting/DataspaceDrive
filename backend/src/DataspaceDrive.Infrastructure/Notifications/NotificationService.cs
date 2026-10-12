using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Notifications;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataspaceDrive.Infrastructure.Notifications;

public sealed class NotificationService(DriveDbContext db) : INotificationService
{
    public async Task<NotificationSummary> SummaryAsync(DrivePrincipal principal, CancellationToken ct)
    {
        int? inboxNew = null, inboxAttention = null, approvals = null;
        if (principal.Roles.Contains(DrivePrincipal.RoleInbox))
        {
            inboxNew = await db.IncomingOffers.CountAsync(o => o.State == IncomingOfferState.New, ct);
            inboxAttention = await db.IncomingOffers.CountAsync(o => o.State == IncomingOfferState.Failed || o.State == IncomingOfferState.Quarantined, ct);
        }
        if (principal.Roles.Contains(DrivePrincipal.RoleApprover))
            approvals = await db.Shares.CountAsync(s => s.Approval == ShareApproval.Pending && s.CreatedBy != principal.UserId, ct);
        var failed = await (from d in db.Deliveries
                            join s in db.Shares on d.ShareId equals s.Id
                            where s.CreatedBy == principal.UserId && d.State == DeliveryState.ProvisioningFailed
                            select d.Id).CountAsync(ct);
        return new NotificationSummary(inboxNew, inboxAttention, approvals, failed);
    }
}
