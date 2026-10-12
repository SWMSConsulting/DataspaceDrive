using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataspaceDrive.Infrastructure.Inbox;

public sealed class InboxService(DriveDbContext db, AccessResolver access, IServiceProvider services, TimeProvider clock) : IInboxService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<IncomingOfferInfo>> ListAsync(DrivePrincipal principal, CancellationToken ct)
    {
        RequireReader(principal);
        var offers = await db.IncomingOffers.OrderByDescending(o => o.FirstSeenAt).Take(500).ToListAsync(ct);
        return await InfosAsync(offers, ct);
    }

    public async Task<IncomingOfferInfo> AcceptAsync(DrivePrincipal principal, Guid offerId, Guid targetFolderId, CancellationToken ct)
    {
        RequireInbox(principal);
        var offer = await FindAsync(offerId, ct);
        if (offer.State != IncomingOfferState.New)
            throw new ConflictException("Das Angebot ist nicht mehr offen.");
        if (offer.ExpiresAt is { } expires && expires <= Now)
            throw new ConflictException("Das Angebot ist abgelaufen.");
        var partner = await db.Partners.FirstAsync(p => p.Id == offer.PartnerId, ct);
        if (partner.Blocked || !partner.ReceivingEnabled)
            throw new ConflictException($"Der Empfang von {partner.Name} ist gesperrt.");
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == targetFolderId && f.DeletedAt == null, ct)
            ?? throw new NotFoundException("Der Zielordner wurde nicht gefunden.");
        await access.RequireAsync(principal, folder.SpaceId, Permission.Write, ct);

        IncomingTransferWorker.MoveTo(offer, IncomingOfferState.Accepted, Now);
        offer.TargetFolderId = folder.Id;
        offer.DecidedBy = principal.UserId;
        offer.DecidedAt = Now;
        offer.Attempts = 0;
        offer.NextAttemptAt = null;
        offer.Error = null;
        offer.ConcurrencyStamp = Guid.NewGuid();
        Audit(principal, "inbox.accepted", offer, folder.Id.ToString());
        await db.SaveChangesAsync(ct);
        return (await InfosAsync([offer], ct))[0];
    }

    public async Task<IncomingOfferInfo> DeclineAsync(DrivePrincipal principal, Guid offerId, CancellationToken ct)
    {
        RequireInbox(principal);
        var offer = await FindAsync(offerId, ct);
        if (offer.State != IncomingOfferState.New)
            throw new ConflictException("Das Angebot ist nicht mehr offen.");
        IncomingTransferWorker.MoveTo(offer, IncomingOfferState.Declined, Now);
        offer.DecidedBy = principal.UserId;
        offer.DecidedAt = Now;
        offer.ConcurrencyStamp = Guid.NewGuid();
        Audit(principal, "inbox.declined", offer, null);
        await db.SaveChangesAsync(ct);
        return (await InfosAsync([offer], ct))[0];
    }

    public async Task<IncomingOfferInfo> RetryAsync(DrivePrincipal principal, Guid offerId, CancellationToken ct)
    {
        RequireInbox(principal);
        var offer = await FindAsync(offerId, ct);
        if (offer.State != IncomingOfferState.Failed)
            throw new ConflictException("Nur eine fehlgeschlagene Übernahme lässt sich wiederholen.");
        // Neue Verhandlung; noch nicht abgelegte Dateien werden vollständig neu geladen.
        foreach (var item in await db.IncomingItems.Where(i => i.OfferId == offer.Id && i.FileVersionId == null).ToListAsync(ct))
            item.ReceivedBytes = 0;
        IncomingTransferWorker.MoveTo(offer, IncomingOfferState.Accepted, Now);
        offer.NegotiationId = null;
        offer.AgreementId = null;
        offer.TransferProcessId = null;
        offer.Attempts = 0;
        offer.NextAttemptAt = null;
        offer.Error = null;
        offer.ConcurrencyStamp = Guid.NewGuid();
        Audit(principal, "inbox.retried", offer, null);
        await db.SaveChangesAsync(ct);
        return (await InfosAsync([offer], ct))[0];
    }

    public Task RequestRefreshAsync(DrivePrincipal principal, CancellationToken ct)
    {
        RequireReader(principal);
        services.GetService<IInboxRefresh>()?.RequestRefresh();
        return Task.CompletedTask;
    }

    /// <summary>Den Eingang sehen Nutzer mit drive-inbox und die Verwaltung.</summary>
    private static void RequireReader(DrivePrincipal principal)
    {
        if (!principal.Roles.Contains(DrivePrincipal.RoleInbox) && !principal.IsAdmin)
            throw new AccessDeniedException("Der Eingang ist für dieses Konto nicht freigeschaltet.");
    }

    private static void RequireInbox(DrivePrincipal principal)
    {
        if (!principal.Roles.Contains(DrivePrincipal.RoleInbox))
            throw new AccessDeniedException("Angebote annehmen oder ablehnen darf nur, wer den Eingang betreut (Rolle drive-inbox).");
    }

    private async Task<IncomingOffer> FindAsync(Guid offerId, CancellationToken ct) =>
        await db.IncomingOffers.FirstOrDefaultAsync(o => o.Id == offerId, ct) ?? throw new NotFoundException("Das Angebot wurde nicht gefunden.");

    private async Task<IReadOnlyList<IncomingOfferInfo>> InfosAsync(IReadOnlyList<IncomingOffer> offers, CancellationToken ct)
    {
        var ids = offers.Select(o => o.Id).ToList();
        var items = await db.IncomingItems.Where(i => ids.Contains(i.OfferId)).ToListAsync(ct);
        var versionIds = items.Where(i => i.FileVersionId != null).Select(i => i.FileVersionId!.Value).ToList();
        var scans = await db.FileVersions.Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.ScanStatus, ct);
        var partnerIds = offers.Select(o => o.PartnerId).Distinct().ToList();
        var partners = await db.Partners.Where(p => partnerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var userIds = offers.Where(o => o.DecidedBy != null).Select(o => o.DecidedBy!.Value).Distinct().ToList();
        var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var folderIds = offers.Where(o => o.TargetFolderId != null).Select(o => o.TargetFolderId!.Value).Distinct().ToList();
        var paths = new Dictionary<Guid, string>();
        foreach (var folderId in folderIds)
            paths[folderId] = await PathAsync(folderId, ct);
        return offers.Select(o => new IncomingOfferInfo(
                o.Id, o.PartnerId, partners.GetValueOrDefault(o.PartnerId, o.ProviderBpn), o.ProviderBpn, o.Title, o.Message, o.SenderOrganization,
                o.ExpiresAt, o.TotalSize, o.State, o.FirstSeenAt, o.TargetFolderId,
                o.TargetFolderId is { } f ? paths.GetValueOrDefault(f) : null,
                o.DecidedBy is { } u ? users.GetValueOrDefault(u) : null, o.DecidedAt, o.Error, o.CompletedAt,
                items.Where(i => i.OfferId == o.Id).OrderBy(i => i.ItemKey.Length).ThenBy(i => i.ItemKey)
                    .Select(i => new IncomingItemInfo(i.ItemKey, i.Name, i.SizeBytes, i.Sha256, i.ContentType, i.ReceivedBytes, i.FileEntryId,
                        i.FileVersionId is { } v && scans.TryGetValue(v, out var s) ? s : null, i.RelativePath))
                    .ToList()))
            .ToList();
    }

    /// <summary>„Bereich / Ordner / Unterordner“ für die Anzeige.</summary>
    private async Task<string> PathAsync(Guid folderId, CancellationToken ct)
    {
        var names = new List<string>();
        Guid? current = folderId;
        for (var depth = 0; current is not null && depth < 50; depth++)
        {
            var folder = await db.Folders.Where(f => f.Id == current).Select(f => new { f.Name, f.ParentId }).FirstOrDefaultAsync(ct);
            if (folder is null)
                break;
            names.Add(folder.Name);
            current = folder.ParentId;
        }
        names.Reverse();
        return string.Join(" / ", names);
    }

    private void Audit(DrivePrincipal principal, string action, IncomingOffer offer, string? details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = Now, ActorUserId = principal.UserId, Action = action, TargetType = "incomingOffer", TargetId = offer.Id.ToString(),
            Details = details,
        });
}
