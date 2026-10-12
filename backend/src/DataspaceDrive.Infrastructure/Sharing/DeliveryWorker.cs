using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swms.Edc.Client;
using Swms.Edc.Client.Policies;

namespace DataspaceDrive.Infrastructure.Sharing;

/// <summary>
/// Arbeitet die EDC-Aufträge der Zustellungen ab (Outbox) und lässt fällige Zustellungen ablaufen. Alle EDC-Anlagen
/// sind idempotent (feste IDs, 409 = vorhanden); ein wiederholter Auftrag richtet keinen Schaden an.
/// </summary>
public sealed class DeliveryWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DeliveryWorker> logger) : BackgroundService
{
    private const int MaxProvisionAttempts = 8;
    private const int MaxNotifyAttempts = 5;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NotifyTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan InboxCheckInterval = TimeSpan.FromMinutes(1);

    private DateTime _lastInboxCheck = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!WorkersEnabled(scopes))
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var worked = await ProcessNextAsync(stoppingToken);
                if (!worked)
                {
                    await ExpireDueAsync(stoppingToken);
                    if (clock.GetUtcNow().UtcDateTime - _lastInboxCheck > InboxCheckInterval)
                    {
                        _lastInboxCheck = clock.GetUtcNow().UtcDateTime;
                        await EnsureInboxAsync(stoppingToken);
                    }
                }
                delay = worked ? TimeSpan.Zero : IdleDelay;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Zustellungen konnten nicht bearbeitet werden; neuer Versuch in {Delay}", ErrorDelay);
                delay = ErrorDelay;
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

    /// <summary>Bearbeitet den ältesten fälligen Auftrag; false, wenn keiner ansteht.</summary>
    internal async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var task = await db.DeliveryTasks.Where(t => t.CompletedAt == null && t.NextAttemptAt <= now).OrderBy(t => t.Id).FirstOrDefaultAsync(ct);
        if (task is null)
            return false;
        var delivery = await db.Deliveries.FirstAsync(d => d.Id == task.DeliveryId, ct);
        var edc = scope.ServiceProvider.GetRequiredService<EdcConnection>().Client;
        try
        {
            if (task.Kind == DeliveryTaskKind.Notify)
            {
                // Benachrichtigung ist Komfort: nach einer Weile still aufgeben, das Polling übernimmt.
                var retryAt = now - task.CreatedAt > NotifyTimeout
                    ? null
                    : await PartnerNotifications.NotifyAsync(scope.ServiceProvider, db, edc, delivery, now, ct);
                if (retryAt is { } at)
                {
                    task.NextAttemptAt = at;
                    await db.SaveChangesAsync(ct);
                    return true;
                }
            }
            else if (task.Kind == DeliveryTaskKind.Provision)
            {
                await ProvisionAsync(scope.ServiceProvider, db, edc, delivery, now, ct);
                if (delivery.State == DeliveryState.Active)
                    db.DeliveryTasks.Add(new DeliveryTask { Kind = DeliveryTaskKind.Notify, DeliveryId = delivery.Id, CreatedAt = now, NextAttemptAt = now });
            }
            else
                await EndAsync(db, edc, delivery, now, ct);
            task.CompletedAt = now;
            task.LastError = null;
        }
        catch (Exception ex) when (ex is EdcException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            task.Attempts++;
            task.LastError = Truncate(ex.Message);
            task.NextAttemptAt = now + Backoff(task.Attempts);
            logger.LogWarning("EDC-Auftrag {Kind} für Zustellung {DeliveryId} fehlgeschlagen (Versuch {Attempt}): {Error}",
                task.Kind, delivery.Id, task.Attempts, ex.Message);
            if (task.Kind == DeliveryTaskKind.Notify && task.Attempts >= MaxNotifyAttempts)
                task.CompletedAt = now;
            // Abbau wird beliebig oft wiederholt, er muss irgendwann gelingen; die Anlage gibt nach einigen Versuchen auf.
            if (task.Kind == DeliveryTaskKind.Provision && task.Attempts >= MaxProvisionAttempts && delivery.State == DeliveryState.Provisioning)
            {
                delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State, DeliveryState.ProvisioningFailed);
                delivery.LastError = task.LastError;
                delivery.ConcurrencyStamp = Guid.NewGuid();
                task.CompletedAt = now;
                Audit(db, now, "delivery.provisioning-failed", delivery, task.LastError);
                await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().DeliveryFailedAsync(delivery, ct);
            }
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Die Zustellung wurde währenddessen geändert (z. B. widerrufen); der nächste Durchlauf liest neu.
            logger.LogInformation("Zustellung {DeliveryId} wurde während der Bearbeitung geändert; erneuter Durchlauf", delivery.Id);
        }
        return true;
    }

    /// <summary>Eigenes Eingangs-Asset mit der aktuellen Partnerliste abgleichen; true, wenn der Connector angepasst wurde.</summary>
    internal async Task<bool> EnsureInboxAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        try
        {
            return await PartnerNotifications.EnsureInboxAsync(scope.ServiceProvider, db, scope.ServiceProvider.GetRequiredService<EdcConnection>().Client,
                clock.GetUtcNow().UtcDateTime, ct);
        }
        catch (Exception ex) when (ex is EdcException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Eingangs-Asset konnte nicht angelegt werden: {Error}", ex.Message);
            return false;
        }
    }

    /// <summary>Aktive Zustellungen nach Ablauf abbauen; fehlgeschlagene, abgelaufene aufräumen.</summary>
    internal async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.Deliveries
            .Where(d => d.ExpiresAt <= now && (d.State == DeliveryState.Active || d.State == DeliveryState.ProvisioningFailed))
            .ToListAsync(ct);
        foreach (var delivery in due)
        {
            delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State,
                delivery.State == DeliveryState.Active ? DeliveryState.Expiring : DeliveryState.Revoking);
            delivery.ConcurrencyStamp = Guid.NewGuid();
            db.DeliveryTasks.Add(new DeliveryTask { Kind = DeliveryTaskKind.End, DeliveryId = delivery.Id, CreatedAt = now, NextAttemptAt = now });
            Audit(db, now, "delivery.expiring", delivery, null);
        }
        // Ohne Prüfung abgelaufen: beenden, im Connector gibt es nichts abzubauen.
        var unreviewed = await db.Deliveries.Where(d => d.ExpiresAt <= now && d.State == DeliveryState.Requested).ToListAsync(ct);
        foreach (var delivery in unreviewed)
        {
            delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State, DeliveryState.Rejected);
            delivery.EndedAt = now;
            delivery.LastError = "abgelaufen ohne Prüfung";
            delivery.ConcurrencyStamp = Guid.NewGuid();
        }
        var shareIds = unreviewed.Select(d => d.ShareId).Distinct().ToList();
        foreach (var share in await db.Shares.Where(s => shareIds.Contains(s.Id) && s.Approval == ShareApproval.Pending).ToListAsync(ct))
        {
            share.Approval = ShareApproval.Rejected;
            share.DecisionNote = "abgelaufen ohne Prüfung";
            share.DecidedAt = now;
        }
        if (due.Count + unreviewed.Count > 0)
            await db.SaveChangesAsync(ct);
        return due.Count + unreviewed.Count;
    }

    private static async Task ProvisionAsync(IServiceProvider services, DriveDbContext db, EdcManagementClient edc, Delivery delivery, DateTime now,
        CancellationToken ct)
    {
        if (delivery.State != DeliveryState.Provisioning)
            return; // inzwischen widerrufen: der End-Auftrag räumt auf
        var share = await db.Shares.FirstAsync(s => s.Id == delivery.ShareId, ct);
        var items = await db.ShareItems.Where(i => i.ShareId == share.Id).ToListAsync(ct);
        var tokens = services.GetRequiredService<DeliveryTokenService>();
        var participant = services.GetRequiredService<IOptions<ParticipantOptions>>().Value;
        var sharing = services.GetRequiredService<IOptions<SharingOptions>>().Value;

        await edc.CreatePolicyAsync(DriveExchange.AccessPolicy(delivery.RecipientBpn), ct);
        await edc.CreatePolicyAsync(DriveExchange.ContractPolicy(), ct);
        await edc.CreateAssetAsync(DriveExchange.DeliveryAsset(new DeliveryAssetSpec(
            delivery.ExternalId, sharing.InstanceId, share.Title, share.Message, delivery.RecipientBpn, participant.Bpn, participant.Organization,
            new DateTimeOffset(DateTime.SpecifyKind(delivery.ExpiresAt, DateTimeKind.Utc)),
            items.OrderBy(i => i.ItemKey.Length).ThenBy(i => i.ItemKey)
                .Select(i => new ManifestItem(i.ItemKey, i.Name, i.SizeBytes, i.Sha256, i.ContentType, i.RelativePath, i.FileEntryId.ToString("N")))
                .ToList(),
            sharing.ContentBaseUrl, tokens.Create(delivery.ExternalId), share.LineageId.ToString("N"), share.Revision)), ct);
        await edc.CreateContractDefinitionAsync(DriveExchange.DeliveryContractDefinition(delivery.ExternalId, delivery.RecipientBpn), ct);

        delivery.TokenVersion = tokens.CurrentVersion;
        delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State, DeliveryState.Active);
        delivery.ActivatedAt = now;
        delivery.LastError = null;
        delivery.ConcurrencyStamp = Guid.NewGuid();
        Audit(db, now, "delivery.activated", delivery, DriveExchange.AssetId(delivery.ExternalId));
    }

    private static async Task EndAsync(DriveDbContext db, EdcManagementClient edc, Delivery delivery, DateTime now, CancellationToken ct)
    {
        var assetId = DriveExchange.AssetId(delivery.ExternalId);
        var reason = delivery.State == DeliveryState.Expiring ? "Freigabe abgelaufen" : "Freigabe widerrufen";
        await edc.DeleteContractDefinitionAsync(DriveExchange.ContractDefinitionId(delivery.ExternalId), ct);
        var agreements = await edc.QueryAgreementsForAssetAsync(assetId, ct);
        foreach (var agreement in agreements)
            await edc.RetireAgreementAsync(agreement.Id, reason, ct);
        // Ohne Agreements verschwindet das Asset, sonst bleibt es als Grabstein ohne Contract Definition stehen.
        var deleted = agreements.Count == 0 && await edc.DeleteAssetAsync(assetId, ct);

        if (delivery.State is DeliveryState.Revoking or DeliveryState.Expiring)
        {
            delivery.State = DeliveryLifecycle.Transitions.Move(delivery.State,
                delivery.State == DeliveryState.Expiring ? DeliveryState.Expired : DeliveryState.Revoked);
            delivery.EndedAt = now;
            delivery.ConcurrencyStamp = Guid.NewGuid();
        }
        Audit(db, now, "delivery.ended", delivery, $"{reason}; {agreements.Count} Agreement(s) zurückgezogen{(deleted ? ", Asset gelöscht" : "")}");
    }

    /// <summary>Nur mit konfiguriertem EDC und eingeschalteten Hintergrunddiensten.</summary>
    internal static bool WorkersEnabled(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<EdcConnection>().IsConfigured
            && scope.ServiceProvider.GetRequiredService<IOptions<BackgroundWorkerOptions>>().Value.Enabled;
    }

    private static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, Math.Min(attempts, 10))));

    private static string Truncate(string text) => text.Length > 1000 ? text[..1000] : text;

    private static void Audit(DriveDbContext db, DateTime now, string action, Delivery delivery, string? details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = now, Action = action, TargetType = "delivery", TargetId = delivery.Id.ToString(), Details = details,
        });
}
