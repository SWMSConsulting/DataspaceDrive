using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Partners;
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
        RequireSharer(principal);
        var fileIds = (request.FileIds ?? []).Distinct().ToList();
        var folderIds = (request.FolderIds ?? []).Distinct().ToList();
        var partnerIds = (request.PartnerIds ?? []).Distinct().ToList();
        if (fileIds.Count == 0 && folderIds.Count == 0)
            throw new InvalidInputException("Bitte mindestens eine Datei oder einen Ordner auswählen.");
        if (partnerIds.Count == 0)
            throw new InvalidInputException("Bitte mindestens einen Partner auswählen.");
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
        var direct = await db.Files.Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null).CountAsync(ct);
        if (direct != fileIds.Count)
            throw new NotFoundException("Mindestens eine Datei wurde nicht gefunden.");

        var share = await CreateRevisionAsync(principal, fileIds, folderIds, partners, request.Title, request.Message, request.ExpiresAt, null, ct);
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync([share], ct))[0];
    }

    public async Task<ShareSummary> UpdateAsync(DrivePrincipal principal, Guid shareId, UpdateShareRequest request, CancellationToken ct)
    {
        RequireSharer(principal);
        var previous = await db.Shares.FirstOrDefaultAsync(s => s.Id == shareId, ct) ?? throw new NotFoundException("Die Freigabe wurde nicht gefunden.");
        RequireOwner(principal, previous);
        if (await db.Shares.AnyAsync(s => s.SupersedesShareId == previous.Id, ct))
            throw new ConflictException("Es gibt bereits eine neuere Revision dieser Freigabe.");
        var current = await db.Deliveries.Where(d => d.ShareId == previous.Id
            && (d.State == DeliveryState.Active || d.State == DeliveryState.Provisioning || d.State == DeliveryState.ProvisioningFailed)).ToListAsync(ct);
        if (current.Count == 0)
            throw new ConflictException("Diese Freigabe wird an niemanden mehr zugestellt; bitte neu teilen.");
        var partnerIds = current.Select(d => d.PartnerId).ToList();
        var partners = (await db.Partners.Where(p => partnerIds.Contains(p.Id)).ToListAsync(ct))
            .Where(p => p.SharingEnabled && !p.Blocked && p.IsResolved).ToList();
        if (partners.Count == 0)
            throw new ConflictException("Keiner der bisherigen Partner darf derzeit beliefert werden.");

        // Ursprüngliche Auswahl im aktuellen Stand: gelöschte Elemente entfallen, neue Dateien in geteilten Ordnern kommen hinzu.
        var fileIds = ParseIds(previous.SourceFileIds);
        var folderIds = ParseIds(previous.SourceFolderIds);
        if (fileIds.Count == 0 && folderIds.Count == 0)
            fileIds = await db.ShareItems.Where(i => i.ShareId == previous.Id).Select(i => i.FileEntryId).Distinct().ToListAsync(ct);
        fileIds = await db.Files.Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null).Select(f => f.Id).ToListAsync(ct);
        folderIds = await db.Folders.Where(f => folderIds.Contains(f.Id) && f.DeletedAt == null).Select(f => f.Id).ToListAsync(ct);
        if (fileIds.Count == 0 && folderIds.Count == 0)
            throw new ConflictException("Die geteilten Dateien und Ordner gibt es nicht mehr.");
        var expiresAt = request.ExpiresAt ?? (previous.ExpiresAt > Now.AddHours(1) ? previous.ExpiresAt : null);

        var share = await CreateRevisionAsync(principal, fileIds, folderIds, partners, previous.Title, request.Message ?? previous.Message, expiresAt,
            previous, ct);
        foreach (var delivery in current)
            Revoke(principal, delivery, $"ersetzt durch Revision {share.Revision}");
        await db.SaveChangesAsync(ct);
        return (await SummariesAsync([share], ct))[0];
    }

    /// <summary>Legt Freigabe, Dateien, Zustellungen und EDC-Aufträge an (ohne zu speichern).</summary>
    private async Task<Share> CreateRevisionAsync(DrivePrincipal principal, IReadOnlyList<Guid> fileIds, IReadOnlyList<Guid> folderIds,
        IReadOnlyList<Partner> partners, string? requestedTitle, string? requestedMessage, DateTime? requestedExpiry, Share? previous,
        CancellationToken ct)
    {
        var direct = await db.Files.Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null).ToListAsync(ct);
        // Einzeln gewählte Dateien liegen beim Empfänger oben, Ordnerinhalte unter ihrem Pfad.
        var entries = fileIds.Where(id => direct.Any(f => f.Id == id))
            .Select(id => (File: direct.Single(f => f.Id == id), Path: (string?)null)).ToList();
        var folderNames = new List<string>();
        foreach (var folderId in folderIds)
        {
            var (name, folderFiles) = await ExpandFolderAsync(principal, folderId, ct);
            folderNames.Add(name);
            entries.AddRange(folderFiles.Where(f => entries.All(e => e.File.Id != f.File.Id)));
        }
        if (entries.Count == 0)
            throw new InvalidInputException("Die gewählten Ordner enthalten keine Dateien.");
        if (entries.Count > _options.MaxItems)
            throw new InvalidInputException($"Eine Freigabe umfasst höchstens {_options.MaxItems} Dateien (gewählt: {entries.Count}).");
        var files = entries.Select(e => e.File).ToList();
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

        var now = Now;
        var expiresAt = requestedExpiry?.ToUniversalTime() ?? now + _options.DefaultLifetime;
        if (expiresAt < now.AddMinutes(5))
            throw new InvalidInputException("Das Ablaufdatum muss in der Zukunft liegen.");
        // Ein Tag Spielraum, damit „heute + maximale Dauer“ aus dem Datumsfeld nicht an der Uhrzeit scheitert.
        if (expiresAt > now + _options.MaxLifetime + TimeSpan.FromDays(1))
            throw new InvalidInputException($"Freigaben laufen höchstens {_options.MaxLifetime.TotalDays:0} Tage.");
        var title = string.IsNullOrWhiteSpace(requestedTitle)
            ? (folderNames.Count == 1 && fileIds.Count == 0 ? folderNames[0] : files.Count == 1 ? files[0].Name : $"{files.Count} Dateien")
            : requestedTitle.Trim();
        if (title.Length > MaxTitleLength)
            throw new InvalidInputException($"Der Titel darf höchstens {MaxTitleLength} Zeichen lang sein.");
        var message = string.IsNullOrWhiteSpace(requestedMessage) ? null : requestedMessage.Trim();
        if (message?.Length > MaxMessageLength)
            throw new InvalidInputException($"Die Nachricht darf höchstens {MaxMessageLength} Zeichen lang sein.");

        var id = Guid.NewGuid();
        var share = new Share
        {
            Id = id, Title = title, Message = message, CreatedBy = principal.UserId, CreatedAt = now, ExpiresAt = expiresAt,
            LineageId = previous?.LineageId ?? id, Revision = (previous?.Revision ?? 0) + 1, SupersedesShareId = previous?.Id,
            SourceFileIds = fileIds.Count == 0 ? null : string.Join(',', fileIds),
            SourceFolderIds = folderIds.Count == 0 ? null : string.Join(',', folderIds),
        };
        db.Shares.Add(share);
        // Reihenfolge wie ausgewählt; die Schlüssel "1", "2", … stehen im Manifest und im Abrufpfad.
        for (var i = 0; i < entries.Count; i++)
        {
            var (file, path) = entries[i];
            var version = versions[file.CurrentVersionId!.Value];
            db.ShareItems.Add(new ShareItem
            {
                Id = Guid.NewGuid(), ShareId = share.Id, ItemKey = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FileEntryId = file.Id, FileVersionId = version.Id, Name = file.Name, SizeBytes = version.SizeBytes,
                Sha256 = version.Sha256, ContentType = version.ContentType, BlobKey = version.BlobKey, RelativePath = path,
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
        Audit(principal, previous is null ? "share.created" : "share.updated", "share", share.Id,
            $"{title} (Revision {share.Revision}): {entries.Count} Datei(en){(folderNames.Count > 0 ? " aus " + string.Join(", ", folderNames) : "")} an "
            + $"{string.Join(", ", partners.Select(p => p.Bpn))}, Ablauf {expiresAt:O}");
        return share;
    }

    private static void RequireSharer(DrivePrincipal principal)
    {
        if (!principal.Roles.Contains(DrivePrincipal.RoleExternalShare))
            throw new AccessDeniedException("Freigaben an andere Unternehmen sind für dieses Konto nicht freigeschaltet.");
    }

    private static List<Guid> ParseIds(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).Distinct().ToList();

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

    /// <summary>
    /// Alle Dateien eines Ordners mit Unterordnern; der Pfad beginnt mit dem Ordnernamen („Begehung/Fotos“).
    /// Die Rechte prüft der Aufrufer je Datei, hier nur das Leserecht auf den Ordner selbst.
    /// </summary>
    private async Task<(string Name, List<(FileEntry File, string? Path)> Files)> ExpandFolderAsync(DrivePrincipal principal, Guid folderId,
        CancellationToken ct)
    {
        const int maxDepth = 20;
        var root = await db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.DeletedAt == null, ct)
            ?? throw new NotFoundException("Mindestens ein Ordner wurde nicht gefunden.");
        await access.RequireAsync(principal, root.SpaceId, Permission.Read, ct);
        var paths = new Dictionary<Guid, string> { [root.Id] = root.Name };
        var frontier = new List<Guid> { root.Id };
        for (var depth = 0; frontier.Count > 0; depth++)
        {
            if (depth >= maxDepth)
                throw new InvalidInputException($"„{root.Name}“ ist tiefer als {maxDepth} Ebenen verschachtelt.");
            var current = frontier;
            var children = await db.Folders.Where(f => f.ParentId != null && current.Contains(f.ParentId.Value) && f.DeletedAt == null)
                .Select(f => new { f.Id, ParentId = f.ParentId!.Value, f.Name }).ToListAsync(ct);
            foreach (var child in children)
                paths[child.Id] = paths[child.ParentId] + "/" + child.Name;
            frontier = children.Select(c => c.Id).ToList();
        }
        var folderIds = paths.Keys.ToList();
        var files = await db.Files.Where(f => folderIds.Contains(f.FolderId) && f.DeletedAt == null).ToListAsync(ct);
        return (root.Name, files.OrderBy(f => paths[f.FolderId], StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => (f, (string?)paths[f.FolderId])).ToList());
    }

    /// <summary>Sperrt die Zustellung sofort (der Content-Endpunkt liefert nur aktive aus) und plant den Abbau im EDC.</summary>
    private void Revoke(DrivePrincipal principal, Delivery delivery, string? reason = null)
    {
        if (delivery.State is not (DeliveryState.Active or DeliveryState.Provisioning or DeliveryState.ProvisioningFailed))
            return; // bereits widerrufen, abgelaufen oder im Abbau
        delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State, DeliveryState.Revoking);
        delivery.RevokedBy = principal.UserId;
        delivery.ConcurrencyStamp = Guid.NewGuid();
        db.DeliveryTasks.Add(new DeliveryTask { Kind = DeliveryTaskKind.End, DeliveryId = delivery.Id, CreatedAt = Now, NextAttemptAt = Now });
        Audit(principal, "delivery.revoked", "delivery", delivery.Id, reason is null ? delivery.RecipientBpn : $"{delivery.RecipientBpn}, {reason}");
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
        var fileIds = items.Select(i => i.FileEntryId).Distinct().ToList();
        var currentVersions = await db.Files.Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null)
            .ToDictionaryAsync(f => f.Id, f => f.CurrentVersionId, ct);
        return shares.Select(s => new ShareSummary(
                s.Id, s.Title, s.Message, s.CreatedBy, userNames.GetValueOrDefault(s.CreatedBy, ""), s.CreatedAt, s.ExpiresAt,
                items.Where(i => i.ShareId == s.Id).OrderBy(i => i.ItemKey.Length).ThenBy(i => i.ItemKey)
                    .Select(i => new ShareItemInfo(i.ItemKey, i.Name, i.SizeBytes, i.Sha256, i.FileEntryId, i.RelativePath)).ToList(),
                deliveries.Where(d => d.ShareId == s.Id).OrderBy(d => partnerNames.GetValueOrDefault(d.PartnerId))
                    .Select(d => new DeliveryInfo(d.Id, d.PartnerId, partnerNames.GetValueOrDefault(d.PartnerId, d.RecipientBpn), d.RecipientBpn,
                        d.State, d.ExpiresAt, d.ActivatedAt, d.EndedAt, d.RetrievalCount, d.LastRetrievalAt, d.LastError))
                    .ToList(),
                s.LineageId, s.Revision,
                items.Any(i => i.ShareId == s.Id && currentVersions.TryGetValue(i.FileEntryId, out var v) && v != i.FileVersionId)))
            .ToList();
    }

    private void Audit(DrivePrincipal principal, string action, string targetType, Guid targetId, string details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = Now, ActorUserId = principal.UserId, Action = action, TargetType = targetType, TargetId = targetId.ToString(),
            Details = details,
        });
}
