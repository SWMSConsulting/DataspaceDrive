using DataspaceDrive.Core.Shares;
using DataspaceDrive.Infrastructure.Notifications;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Swms.Edc.Client;
using Swms.Edc.Client.Policies;

namespace DataspaceDrive.Infrastructure.Sharing;

/// <summary>
/// Zustellungen aus der Datenbank, Agreements aus dem eigenen EDC. Agreements werden kurz zwischengespeichert, weil
/// ein segmentierter Abruf viele Anfragen stellt; ein Widerruf wirkt trotzdem sofort, denn er ändert zuerst den
/// Zustand der Zustellung.
/// </summary>
public sealed class DeliveryDirectory(DriveDbContext db, IServiceProvider services, IMemoryCache cache, Notifier notifier, TimeProvider clock)
    : IDeliveryDirectory
{
    private static readonly TimeSpan AgreementCacheTime = TimeSpan.FromSeconds(30);

    public async Task<DeliverySnapshot?> FindDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(deliveryId, "N", out var id))
            return null;
        var row = await (from d in db.Deliveries
                         join p in db.Partners on d.PartnerId equals p.Id
                         where d.Id == id
                         select new { d.State, d.ExpiresAt, d.RecipientBpn, p.Blocked }).FirstOrDefaultAsync(ct);
        return row is null
            ? null
            : new DeliverySnapshot(deliveryId, row.State, new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc)), row.RecipientBpn,
                row.Blocked, DriveExchange.AssetId(deliveryId));
    }

    public async Task<AgreementSnapshot?> FindAgreementAsync(string agreementId, CancellationToken ct)
    {
        if (cache.TryGetValue<AgreementSnapshot?>(CacheKey(agreementId), out var cached))
            return cached;
        var edc = services.GetRequiredService<EdcConnection>().TryClient;
        if (edc is null)
            return null;
        var agreement = await edc.GetAgreementAsync(agreementId, ct);
        var snapshot = agreement is null
            ? null
            : new AgreementSnapshot(agreement.Id, agreement.AssetId, await edc.IsAgreementRetiredAsync(agreement.Id, ct));
        cache.Set(CacheKey(agreementId), snapshot, AgreementCacheTime);
        return snapshot;
    }

    public async Task<DeliveryItem?> FindItemAsync(string deliveryId, string? itemId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(deliveryId, "N", out var id))
            return null;
        var shareId = await db.Deliveries.Where(d => d.Id == id).Select(d => (Guid?)d.ShareId).FirstOrDefaultAsync(ct);
        if (shareId is null)
            return null;
        var items = itemId is null
            ? await db.ShareItems.Where(i => i.ShareId == shareId).Take(2).ToListAsync(ct)
            : await db.ShareItems.Where(i => i.ShareId == shareId && i.ItemKey == itemId).ToListAsync(ct);
        // Ohne Datei-Schlüssel nur, wenn die Zustellung genau eine Datei hat.
        return items.Count == 1 ? new DeliveryItem(items[0].ItemKey, items[0].BlobKey, items[0].Name, items[0].ContentType, items[0].SizeBytes) : null;
    }

    public async Task RecordRetrievalAsync(string deliveryId, DeliveryAccessDecision decision, string? consumerBpn, string? agreementId,
        string? itemKey, long? offset, long? length, CancellationToken ct)
    {
        if (!Guid.TryParseExact(deliveryId, "N", out var id) || !await db.Deliveries.AnyAsync(d => d.Id == id, ct))
            return; // unbekannte Zustellungen landen nur im Log
        var now = clock.GetUtcNow().UtcDateTime;
        db.DeliveryRetrievals.Add(new DeliveryRetrieval
        {
            DeliveryId = id, OccurredAt = now, Decision = decision.ToString(), ConsumerBpn = Truncate(consumerBpn, 100),
            AgreementId = Truncate(agreementId, 200), ItemKey = Truncate(itemKey, 20), Offset = offset, Length = length,
        });
        await db.SaveChangesAsync(ct);
        if (decision == DeliveryAccessDecision.Allowed && (offset ?? 0) == 0)
        {
            var first = await db.Deliveries.Where(d => d.Id == id).Select(d => d.RetrievalCount).FirstAsync(ct) == 0;
            await db.Deliveries.Where(d => d.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.RetrievalCount, d => d.RetrievalCount + 1).SetProperty(d => d.LastRetrievalAt, now), ct);
            if (first)
            {
                await notifier.FirstRetrievalAsync(id, ct);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private static string CacheKey(string agreementId) => "agreement:" + agreementId;

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v ? (v.Length > max ? v[..max] : v) : null;
}
