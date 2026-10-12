using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Infrastructure.Sharing;

public sealed class ShareService(
    DriveDbContext db,
    AccessResolver access,
    IOptions<SharingOptions> options,
    TimeProvider clock) : IShareService
{
    private const int MaxTitleLength = 200;
    private const int MaxMessageLength = 2000;

    private readonly SharingOptions _options = options.Value;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<ShareSummary> CreateAsync(DrivePrincipal principal, CreateShareRequest request, CancellationToken ct)
    {
        if (!principal.Roles.Contains(DrivePrincipal.RoleExternalShare))
            throw new AccessDeniedException("Freigaben an andere Unternehmen sind für dieses Konto nicht freigeschaltet.");
        var fileIds = (request.FileIds ?? []).Distinct().ToList();
        var partnerIds = (request.PartnerIds ?? []).Distinct().ToList();
        if (fileIds.Count == 0)
            throw new InvalidInputException("Bitte mindestens eine Datei auswählen.");
        if (fileIds.Count > _options.MaxItems)
            throw new InvalidInputException($"Eine Freigabe umfasst höchstens {_options.MaxItems} Dateien.");
        if (partnerIds.Count == 0)
            throw new InvalidInputException("Bitte mindestens einen Partner auswählen.");

        var files = await db.Files.Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null).ToListAsync(ct);
        if (files.Count != fileIds.Count)
            throw new NotFoundException("Mindestens eine Datei wurde nicht gefunden.");
        var versionIds = files.Select(f => f.CurrentVersionId).ToList();
        var versions = await db.FileVersions.Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        foreach (var file in files)
        {
            var space = await access.RequireAsync(principal, file.SpaceId, Permission.Read, ct);
            if (space.Kind == SpaceKind.Team && !space.ExternalSharingAllowed)
                throw new AccessDeniedException($"Aus dem Bereich „{space.Name}“ darf nicht an Partner freigegeben werden.");
            if (file.CurrentVersionId is not { } versionId || !versions.TryGetValue(versionId, out var version) || !version.IsAvailable)
                throw new ConflictException($"„{file.Name}“ ist noch nicht auf Viren geprüft oder gesperrt und kann nicht geteilt werden.");
        }

        var partners = await db.Partners.Where(p => partnerIds.Contains(p.Id)).ToListAsync(ct);
        if (partners.Count != partnerIds.Count)
            throw new NotFoundException("Mindestens ein Partner wurde nicht gefunden.");
        foreach (var partner in partners)
        {
            if (!partner.SharingEnabled || partner.Blocked)
                throw new InvalidInputException($"An {partner.Name} darf derzeit nicht freigegeben werden.");
            if (!partner.IsResolved)
                throw new InvalidInputException($"{partner.Name} ist noch nicht im Dataspace aufgelöst; bitte die Verwaltung informieren.");
        }

        var now = Now;
        var expiresAt = request.ExpiresAt?.ToUniversalTime() ?? now + _options.DefaultLifetime;
        if (expiresAt < now.AddMinutes(5))
            throw new InvalidInputException("Das Ablaufdatum muss in der Zukunft liegen.");
        // Ein Tag Spielraum, damit „heute + maximale Dauer“ aus dem Datumsfeld nicht an der Uhrzeit scheitert.
        if (expiresAt > now + _options.MaxLifetime + TimeSpan.FromDays(1))
            throw new InvalidInputException($"Freigaben laufen höchstens {_options.MaxLifetime.TotalDays:0} Tage.");
        var title = string.IsNullOrWhiteSpace(request.Title)
            ? (files.Count == 1 ? files[0].Name : $"{files.Count} Dateien")
            : request.Title.Trim();
        if (title.Length > MaxTitleLength)
            throw new InvalidInputException($"Der Titel darf höchstens {MaxTitleLength} Zeichen lang sein.");
        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        if (message?.Length > MaxMessageLength)
            throw new InvalidInputException($"Die Nachricht darf höchstens {MaxMessageLength} Zeichen lang sein.");

        var share = new Share { Id = Guid.NewGuid(), Title = title, Message = message, CreatedBy = principal.UserId, CreatedAt = now, ExpiresAt = expiresAt };
        db.Shares.Add(share);
        // Reihenfolge wie ausgewählt; die Schlüssel "1", "2", … stehen im Manifest und im Abrufpfad.
        var ordered = fileIds.Select(id => files.Single(f => f.Id == id)).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var version = versions[ordered[i].CurrentVersionId!.Value];
            db.ShareItems.Add(new ShareItem
            {
                Id = Guid.NewGuid(), ShareId = share.Id, ItemKey = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FileEntryId = ordered[i].Id, FileVersionId = version.Id, Name = ordered[i].Name, SizeBytes = version.SizeBytes,
                Sha256 = version.Sha256, ContentType = version.ContentType, BlobKey = version.BlobKey,
            });
        }
        foreach (var partner in partners)
        {
            var delivery = new Delivery
            {
                Id = Guid.NewGuid(), ShareId = share.Id, PartnerId = partner.Id, RecipientBpn = partner.Bpn, State = DeliveryState.Provisioning,
                ExpiresAt = expiresAt, CreatedAt = now,
            };
            db.Deliveries.Add(delivery);
            db.DeliveryTasks.Add(new DeliveryTask { Kind = DeliveryTaskKind.Provision, DeliveryId = delivery.Id, CreatedAt = now, NextAttemptAt = now });
        }
        Audit(principal, "share.created", "share", share.Id,
            $"{title}: {ordered.Count} Datei(en) an {string.Join(", ", partners.Select(p => p.Bpn))}, Ablauf {expiresAt:O}");
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync([share], ct))[0];
    }

    public async Task<IReadOnlyList<ShareSummary>> ListAsync(DrivePrincipal principal, bool all, CancellationToken ct)
    {
        var query = db.Shares.AsQueryable();
        if (!(all && principal.IsAdmin))
            query = query.Where(s => s.CreatedBy == principal.UserId);
        var shares = await query.OrderByDescending(s => s.CreatedAt).Take(500).ToListAsync(ct);
        return await SummariesAsync(shares, ct);
    }

    public async Task<IReadOnlyList<RetrievalInfo>> RetrievalsAsync(DrivePrincipal principal, Guid deliveryId, CancellationToken ct)
    {
        var (delivery, _) = await OwnDeliveryAsync(principal, deliveryId, ct);
        return await db.DeliveryRetrievals.Where(r => r.DeliveryId == delivery.Id)
            .OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Take(500)
            .Select(r => new RetrievalInfo(r.OccurredAt, r.Decision, r.ConsumerBpn, r.AgreementId, r.ItemKey, r.Offset, r.Length))
            .ToListAsync(ct);
    }

    public async Task<DeliveryInfo> RevokeDeliveryAsync(DrivePrincipal principal, Guid deliveryId, CancellationToken ct)
    {
        var (delivery, share) = await OwnDeliveryAsync(principal, deliveryId, ct);
        Revoke(principal, delivery);
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync([share], ct))[0].Deliveries.Single(d => d.Id == delivery.Id);
    }

    public async Task<ShareSummary> RevokeShareAsync(DrivePrincipal principal, Guid shareId, CancellationToken ct)
    {
        var share = await db.Shares.FirstOrDefaultAsync(s => s.Id == shareId, ct) ?? throw new NotFoundException("Die Freigabe wurde nicht gefunden.");
        RequireOwner(principal, share);
        foreach (var delivery in await db.Deliveries.Where(d => d.ShareId == share.Id).ToListAsync(ct))
            Revoke(principal, delivery);
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync([share], ct))[0];
    }

    public async Task<DeliveryInfo> RetryDeliveryAsync(DrivePrincipal principal, Guid deliveryId, CancellationToken ct)
    {
        var (delivery, share) = await OwnDeliveryAsync(principal, deliveryId, ct);
        if (delivery.State != DeliveryState.ProvisioningFailed)
            throw new ConflictException("Nur eine fehlgeschlagene Zustellung lässt sich erneut anlegen.");
        if (delivery.ExpiresAt <= Now)
            throw new ConflictException("Die Freigabe ist bereits abgelaufen.");
        delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State, DeliveryState.Provisioning);
        delivery.LastError = null;
        delivery.ConcurrencyStamp = Guid.NewGuid();
        db.DeliveryTasks.Add(new DeliveryTask { Kind = DeliveryTaskKind.Provision, DeliveryId = delivery.Id, CreatedAt = Now, NextAttemptAt = Now });
        Audit(principal, "delivery.retried", "delivery", delivery.Id, delivery.RecipientBpn);
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync([share], ct))[0].Deliveries.Single(d => d.Id == delivery.Id);
    }

    /// <summary>Sperrt die Zustellung sofort (der Content-Endpunkt liefert nur aktive aus) und plant den Abbau im EDC.</summary>
    private void Revoke(DrivePrincipal principal, Delivery delivery)
    {
        if (delivery.State is not (DeliveryState.Active or DeliveryState.Provisioning or DeliveryState.ProvisioningFailed))
            return; // bereits widerrufen, abgelaufen oder im Abbau
        delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State, DeliveryState.Revoking);
        delivery.RevokedBy = principal.UserId;
        delivery.ConcurrencyStamp = Guid.NewGuid();
        db.DeliveryTasks.Add(new DeliveryTask { Kind = DeliveryTaskKind.End, DeliveryId = delivery.Id, CreatedAt = Now, NextAttemptAt = Now });
        Audit(principal, "delivery.revoked", "delivery", delivery.Id, delivery.RecipientBpn);
    }

    private async Task<(Delivery Delivery, Share Share)> OwnDeliveryAsync(DrivePrincipal principal, Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct)
            ?? throw new NotFoundException("Die Zustellung wurde nicht gefunden.");
        var share = await db.Shares.FirstAsync(s => s.Id == delivery.ShareId, ct);
        RequireOwner(principal, share);
        return (delivery, share);
    }

    /// <summary>Freigaben sieht und widerruft, wer sie angelegt hat, sowie die Verwaltung.</summary>
    private static void RequireOwner(DrivePrincipal principal, Share share)
    {
        if (share.CreatedBy != principal.UserId && !principal.IsAdmin)
            throw new NotFoundException("Die Freigabe wurde nicht gefunden.");
    }

    private async Task<IReadOnlyList<ShareSummary>> SummariesAsync(IReadOnlyList<Share> shares, CancellationToken ct)
    {
        var ids = shares.Select(s => s.Id).ToList();
        var items = await db.ShareItems.Where(i => ids.Contains(i.ShareId)).ToListAsync(ct);
        var deliveries = await db.Deliveries.Where(d => ids.Contains(d.ShareId)).ToListAsync(ct);
        var partnerIds = deliveries.Select(d => d.PartnerId).Distinct().ToList();
        var partnerNames = await db.Partners.Where(p => partnerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var userIds = shares.Select(s => s.CreatedBy).Distinct().ToList();
        var userNames = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return shares.Select(s => new ShareSummary(
                s.Id, s.Title, s.Message, s.CreatedBy, userNames.GetValueOrDefault(s.CreatedBy, ""), s.CreatedAt, s.ExpiresAt,
                items.Where(i => i.ShareId == s.Id).OrderBy(i => i.ItemKey.Length).ThenBy(i => i.ItemKey)
                    .Select(i => new ShareItemInfo(i.ItemKey, i.Name, i.SizeBytes, i.Sha256, i.FileEntryId)).ToList(),
                deliveries.Where(d => d.ShareId == s.Id).OrderBy(d => partnerNames.GetValueOrDefault(d.PartnerId))
                    .Select(d => new DeliveryInfo(d.Id, d.PartnerId, partnerNames.GetValueOrDefault(d.PartnerId, d.RecipientBpn), d.RecipientBpn,
                        d.State, d.ExpiresAt, d.ActivatedAt, d.EndedAt, d.RetrievalCount, d.LastRetrievalAt, d.LastError))
                    .ToList()))
            .ToList();
    }

    private void Audit(DrivePrincipal principal, string action, string targetType, Guid targetId, string details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = Now, ActorUserId = principal.UserId, Action = action, TargetType = targetType, TargetId = targetId.ToString(),
            Details = details,
        });
}
