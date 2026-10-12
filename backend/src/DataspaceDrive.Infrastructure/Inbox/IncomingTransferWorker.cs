using System.Net;
using DataspaceDrive.Core;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Partners;
using DataspaceDrive.Core.Scanning;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swms.Edc.Client;
using Swms.Edc.Client.Policies;
using Swms.Edc.Client.Requests;

namespace DataspaceDrive.Infrastructure.Inbox;

/// <summary>
/// Übernimmt angenommene Zustellungen: Verhandlung über <c>/v3/edrs</c> mit der Profil-Policy, EDR, Abruf in Abschnitten
/// über die Dataplane des Anbieters, Prüfung von Größe und SHA-256 gegen das Manifest, Ablage im Zielordner und
/// Virenscan. Jeder Schritt hält seinen Fortschritt in der Datenbank; nach einem Neustart geht es dort weiter.
/// Angebote werden nacheinander bearbeitet, damit die Dataplane des Anbieters nicht überlastet wird.
/// </summary>
public sealed class IncomingTransferWorker(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IOptions<InboxOptions> options,
    TimeProvider clock,
    ILogger<IncomingTransferWorker> logger) : BackgroundService
{
    public const string DataplaneClient = "dataplane";

    private const int MaxAttempts = 5;
    private static readonly TimeSpan PollStep = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);

    private static readonly IncomingOfferState[] Active =
    [
        IncomingOfferState.Accepted, IncomingOfferState.Negotiating, IncomingOfferState.AwaitingTransfer, IncomingOfferState.Downloading,
        IncomingOfferState.Verifying,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!DeliveryWorker.WorkersEnabled(scopes))
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.Zero;
            try
            {
                if (!await ProcessNextAsync(stoppingToken))
                {
                    await CompleteScannedAsync(stoppingToken);
                    delay = IdleDelay;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Eingang konnte nicht bearbeitet werden");
                delay = TimeSpan.FromSeconds(15);
            }
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, clock, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Führt für das nächste fällige Angebot einen Schritt aus; false, wenn nichts ansteht.</summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var offer = await db.IncomingOffers
            .Where(o => Active.Contains(o.State) && (o.NextAttemptAt == null || o.NextAttemptAt <= now))
            .OrderBy(o => o.DecidedAt)
            .FirstOrDefaultAsync(ct);
        if (offer is null)
            return false;
        var partner = await db.Partners.FirstAsync(p => p.Id == offer.PartnerId, ct);
        var step = new TransferStep(scope.ServiceProvider, db, offer, partner, now);
        try
        {
            if (partner.Blocked || !partner.IsResolved)
                throw new TransferRefusedException("Der Partner ist gesperrt oder nicht aufgelöst.");
            switch (offer.State)
            {
                case IncomingOfferState.Accepted:
                    await StartNegotiationAsync(step, ct);
                    break;
                case IncomingOfferState.Negotiating:
                    await AwaitAgreementAsync(step, ct);
                    break;
                case IncomingOfferState.AwaitingTransfer:
                    await AwaitTransferAsync(step, ct);
                    break;
                case IncomingOfferState.Downloading:
                    await DownloadAsync(step, ct);
                    break;
                case IncomingOfferState.Verifying:
                    await VerifyAndStoreAsync(step, ct);
                    break;
            }
        }
        catch (TransferRefusedException ex)
        {
            Fail(db, offer, now, ex.Message);
        }
        catch (Exception ex) when (ex is EdcException or HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            offer.Attempts++;
            offer.Error = Truncate(ex.Message);
            offer.NextAttemptAt = now + TimeSpan.FromSeconds(Math.Min(300, 10 * Math.Pow(2, offer.Attempts)));
            logger.LogWarning("Übernahme von {OfferId} im Schritt {State} fehlgeschlagen (Versuch {Attempt}): {Error}",
                offer.Id, offer.State, offer.Attempts, ex.Message);
            if (offer.Attempts >= MaxAttempts)
                Fail(db, offer, now, offer.Error);
        }
        offer.ConcurrencyStamp = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Schließt Angebote ab, deren Dateien der Virenscan inzwischen geprüft hat.</summary>
    internal async Task<int> CompleteScannedAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var finished = 0;
        foreach (var offer in await db.IncomingOffers.Where(o => o.State == IncomingOfferState.Scanning).ToListAsync(ct))
        {
            var versionIds = await db.IncomingItems.Where(i => i.OfferId == offer.Id && i.FileVersionId != null).Select(i => i.FileVersionId!.Value).ToListAsync(ct);
            var statuses = await db.FileVersions.Where(v => versionIds.Contains(v.Id)).Select(v => v.ScanStatus).ToListAsync(ct);
            if (statuses.Contains(ScanStatus.Pending))
                continue;
            var clean = statuses.All(s => s is ScanStatus.Clean or ScanStatus.NotScanned);
            MoveTo(offer, clean ? IncomingOfferState.Completed : IncomingOfferState.Quarantined, now);
            offer.CompletedAt = now;
            offer.Error = clean ? null : "Mindestens eine Datei ist nach dem Virenscan gesperrt.";
            offer.ConcurrencyStamp = Guid.NewGuid();
            Audit(db, now, clean ? "inbox.completed" : "inbox.quarantined", offer, null);
            finished++;
        }
        if (finished > 0)
            await db.SaveChangesAsync(ct);
        return finished;
    }

    private static async Task StartNegotiationAsync(TransferStep step, CancellationToken ct)
    {
        var request = EdcRequests.ContractRequest(step.Offer.OfferId, step.Partner.Did!, step.Offer.AssetId, step.Partner.ProtocolAddress!,
            step.Partner.Protocol!, ExchangeProfile.MembershipContractPermissions());
        step.Offer.NegotiationId = await step.Edc.StartEdrNegotiationAsync(request, ct);
        MoveTo(step.Offer, IncomingOfferState.Negotiating, step.Now);
        step.Offer.NextAttemptAt = step.Now + PollStep;
        step.Offer.Error = null;
        Audit(step.Db, step.Now, "inbox.negotiating", step.Offer, step.Offer.NegotiationId);
    }

    private static async Task AwaitAgreementAsync(TransferStep step, CancellationToken ct)
    {
        var negotiation = await step.Edc.GetNegotiationAsync(step.Offer.NegotiationId!, ct);
        if (negotiation.IsFinalized)
        {
            step.Offer.AgreementId = negotiation.AgreementId;
            MoveTo(step.Offer, IncomingOfferState.AwaitingTransfer, step.Now);
            step.Offer.NextAttemptAt = step.Now;
            return;
        }
        if (negotiation.IsTerminated)
            throw new TransferRefusedException($"Der Anbieter hat die Verhandlung abgelehnt{(negotiation.ErrorDetail is { } d ? ": " + d : ".")}");
        WaitOrTimeout(step, "Die Verhandlung wurde nicht rechtzeitig abgeschlossen.");
    }

    private static async Task AwaitTransferAsync(TransferStep step, CancellationToken ct)
    {
        var transferProcessId = await step.Edc.FindEdrTransferProcessAsync(step.Offer.NegotiationId!, ct);
        if (transferProcessId is null)
        {
            WaitOrTimeout(step, "Der Transfer wurde nicht rechtzeitig bereitgestellt.");
            return;
        }
        step.Offer.TransferProcessId = transferProcessId;
        MoveTo(step.Offer, IncomingOfferState.Downloading, step.Now);
        step.Offer.NextAttemptAt = step.Now;
    }

    /// <summary>Lädt alle Dateien in Abschnitten in die Upload-Ablage; der Fortschritt steht je Datei in der Datenbank.</summary>
    private async Task DownloadAsync(TransferStep step, CancellationToken ct)
    {
        var staging = step.Services.GetRequiredService<IUploadStaging>();
        var http = httpClients.CreateClient(DataplaneClient);
        var address = await step.Edc.GetEdrDataAddressAsync(step.Offer.TransferProcessId!, ct)
            ?? throw new TransferRefusedException("Der Connector liefert keine Zugangsdaten für den Abruf.");
        var segmentSize = Math.Max(1024 * 1024, options.Value.SegmentSize);
        var items = await step.Db.IncomingItems.Where(i => i.OfferId == step.Offer.Id && i.FileVersionId == null).OrderBy(i => i.ItemKey).ToListAsync(ct);
        foreach (var item in items)
        {
            if (item.SizeBytes == 0)
            {
                await staging.WriteChunkAsync(item.Id, 0, Stream.Null, 0, ct);
                continue;
            }
            var refreshed = false;
            while (item.ReceivedBytes < item.SizeBytes)
            {
                var length = Math.Min(segmentSize, item.SizeBytes - item.ReceivedBytes);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"{address.Endpoint.TrimEnd('/')}/items/{Uri.EscapeDataString(item.ItemKey)}?offset={item.ReceivedBytes}&length={length}");
                request.Headers.TryAddWithoutValidation("Authorization", address.Authorization);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    // Abgelaufenes EDR-Token einmal erneuern; danach verweigert der Anbieter wirklich (Widerruf, Ablauf).
                    if (!refreshed && response.StatusCode != HttpStatusCode.Gone)
                    {
                        refreshed = true;
                        address = await step.Edc.GetEdrDataAddressAsync(step.Offer.TransferProcessId!, ct) ?? address;
                        continue;
                    }
                    throw new TransferRefusedException($"Der Anbieter verweigert den Abruf ({(int)response.StatusCode}); die Freigabe wurde vermutlich widerrufen.");
                }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Abruf von „{item.Name}“ ab Byte {item.ReceivedBytes}: HTTP {(int)response.StatusCode}");
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                long written;
                try
                {
                    written = await staging.WriteChunkAsync(item.Id, item.ReceivedBytes, body, length, ct);
                }
                catch (DomainException ex)
                {
                    // Ablage passt nicht zum Fortschritt (z. B. Datei verloren) oder zu viele Bytes: neu beginnen.
                    item.ReceivedBytes = 0;
                    await step.Db.SaveChangesAsync(ct);
                    throw new IOException($"Abruf von „{item.Name}“ wird neu begonnen: {ex.Message}");
                }
                if (written != length)
                    throw new IOException($"Abschnitt von „{item.Name}“ unvollständig: {written} von {length} Bytes.");
                item.ReceivedBytes += written;
                step.Offer.Attempts = 0;
                await step.Db.SaveChangesAsync(ct);
            }
        }
        MoveTo(step.Offer, IncomingOfferState.Verifying, step.Now);
        step.Offer.NextAttemptAt = step.Now;
    }

    /// <summary>Prüft Größe und SHA-256 gegen das Manifest und legt die Dateien im Zielordner ab (Virenscan folgt).</summary>
    private async Task VerifyAndStoreAsync(TransferStep step, CancellationToken ct)
    {
        var db = step.Db;
        var staging = step.Services.GetRequiredService<IUploadStaging>();
        var blobs = step.Services.GetRequiredService<IBlobStore>();
        var scanner = step.Services.GetRequiredService<IVirusScanner>();
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == step.Offer.TargetFolderId && f.DeletedAt == null, ct)
            ?? throw new TransferRefusedException("Der Zielordner existiert nicht mehr.");
        var items = await db.IncomingItems.Where(i => i.OfferId == step.Offer.Id && i.FileVersionId == null).OrderBy(i => i.ItemKey).ToListAsync(ct);
        var folders = new Dictionary<string, Folder>(StringComparer.Ordinal) { [""] = folder };
        foreach (var item in items)
        {
            var versionId = Guid.NewGuid();
            var target = await EnsureFolderAsync(db, folders, item.RelativePath, step.Offer.DecidedBy!.Value, step.Now, ct);
            var blob = await staging.PromoteAsync(item.Id, $"blobs/{versionId:N}", ct);
            if (blob.Length != item.SizeBytes || !string.Equals(blob.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await blobs.DeleteAsync(blob.Key, CancellationToken.None);
                item.ReceivedBytes = 0;
                throw new TransferRefusedException($"„{item.Name}“ weicht vom Manifest ab (Größe oder SHA-256); die Datei wurde verworfen.");
            }
            var name = await FreeNameAsync(db, target.Id, item.Name, ct);
            var file = new FileEntry
            {
                Id = Guid.NewGuid(), SpaceId = target.SpaceId, FolderId = target.Id, Name = name, NormalizedName = ItemName.Normalize(name),
                CurrentVersionId = versionId, CreatedAt = step.Now, CreatedBy = step.Offer.DecidedBy!.Value, ModifiedAt = step.Now,
            };
            db.Files.Add(file);
            db.FileVersions.Add(new FileVersion
            {
                Id = versionId, FileEntryId = file.Id, VersionNo = 1, BlobKey = blob.Key, SizeBytes = blob.Length, Sha256 = blob.Sha256,
                ContentType = item.ContentType, ScanStatus = scanner.Enabled ? ScanStatus.Pending : ScanStatus.NotScanned,
                UploadedBy = step.Offer.DecidedBy!.Value, UploadedAt = step.Now,
            });
            item.FileEntryId = file.Id;
            item.FileVersionId = versionId;
            Audit(db, step.Now, "inbox.file-stored", step.Offer, $"{name}, {blob.Length} Bytes, SHA-256 {blob.Sha256}");
            await db.SaveChangesAsync(ct);
        }
        MoveTo(step.Offer, IncomingOfferState.Scanning, step.Now);
        step.Offer.NextAttemptAt = null;
        try
        {
            // Den Transfer beim Anbieter beenden; er wird nicht mehr gebraucht.
            await step.Edc.TerminateTransferAsync(step.Offer.TransferProcessId!, "Abruf abgeschlossen", ct);
        }
        catch (Exception ex) when (ex is EdcException or HttpRequestException)
        {
            logger.LogInformation("Transfer {TransferProcessId} konnte nicht beendet werden: {Error}", step.Offer.TransferProcessId, ex.Message);
        }
    }

    /// <summary>
    /// Legt den relativen Ordnerpfad unter dem Zielordner an oder nutzt vorhandene gleichnamige Ordner. Heißt eine Datei
    /// so wie ein benötigter Ordner, bekommt der Ordner einen Zähler.
    /// </summary>
    private static async Task<Folder> EnsureFolderAsync(DriveDbContext db, Dictionary<string, Folder> folders, string? relativePath, Guid userId,
        DateTime now, CancellationToken ct)
    {
        var current = folders[""];
        var key = "";
        foreach (var segment in (relativePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            key = key.Length == 0 ? segment : key + "/" + segment;
            if (folders.TryGetValue(key, out var known))
            {
                current = known;
                continue;
            }
            var normalized = ItemName.Normalize(segment);
            var parentId = current.Id;
            var existing = await db.Folders.FirstOrDefaultAsync(f => f.ParentId == parentId && f.DeletedAt == null && f.NormalizedName == normalized, ct);
            if (existing is null)
            {
                var name = await FreeNameAsync(db, parentId, segment, ct);
                existing = new Folder
                {
                    Id = Guid.NewGuid(), SpaceId = current.SpaceId, ParentId = parentId, Name = name, NormalizedName = ItemName.Normalize(name),
                    CreatedAt = now, CreatedBy = userId, ModifiedAt = now,
                };
                db.Folders.Add(existing);
                await db.SaveChangesAsync(ct);
            }
            folders[key] = current = existing;
        }
        return current;
    }

    private static async Task<string> FreeNameAsync(DriveDbContext db, Guid folderId, string name, CancellationToken ct)
    {
        var taken = (await db.Files.Where(f => f.FolderId == folderId && f.DeletedAt == null).Select(f => f.NormalizedName).ToListAsync(ct))
            .Concat(await db.Folders.Where(f => f.ParentId == folderId && f.DeletedAt == null).Select(f => f.NormalizedName).ToListAsync(ct))
            .ToHashSet();
        var candidate = name;
        for (var counter = 2; taken.Contains(ItemName.Normalize(candidate)); counter++)
            candidate = ItemName.WithCounter(name, counter);
        return candidate;
    }

    private static void WaitOrTimeout(TransferStep step, string timeoutMessage)
    {
        if (step.Now - step.Offer.StateChangedAt > StepTimeout)
            throw new TransferRefusedException(timeoutMessage);
        step.Offer.NextAttemptAt = step.Now + PollStep;
    }

    internal static void MoveTo(IncomingOffer offer, IncomingOfferState state, DateTime now)
    {
        offer.State = IncomingOfferLifecycle.Transitions.Move(offer.State, state);
        offer.StateChangedAt = now;
    }

    private static void Fail(DriveDbContext db, IncomingOffer offer, DateTime now, string? message)
    {
        if (IncomingOfferLifecycle.Transitions.CanMove(offer.State, IncomingOfferState.Failed))
            MoveTo(offer, IncomingOfferState.Failed, now);
        offer.Error = Truncate(message ?? "Unbekannter Fehler");
        offer.NextAttemptAt = null;
        Audit(db, now, "inbox.failed", offer, offer.Error);
    }

    private static string Truncate(string text) => text.Length > 1000 ? text[..1000] : text;

    private static void Audit(DriveDbContext db, DateTime now, string action, IncomingOffer offer, string? details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = now, ActorUserId = action == "inbox.file-stored" ? offer.DecidedBy : null, Action = action, TargetType = "incomingOffer",
            TargetId = offer.Id.ToString(), Details = details,
        });

    /// <summary>Kontext eines Bearbeitungsschritts.</summary>
    private sealed record TransferStep(IServiceProvider Services, DriveDbContext Db, IncomingOffer Offer, Partner Partner, DateTime Now)
    {
        public EdcManagementClient Edc { get; } = Services.GetRequiredService<EdcConnection>().Client;
    }

    /// <summary>Endgültige Ablehnung oder Abweichung: kein automatischer neuer Versuch.</summary>
    private sealed class TransferRefusedException(string message) : Exception(message);
}
