using System.Threading.Channels;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Partners;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Swms.Edc.Client;
using Swms.Edc.Client.Policies;

namespace DataspaceDrive.Infrastructure.Inbox;

/// <summary>Auslöser für eine sofortige Katalogabfrage („Jetzt prüfen“).</summary>
public interface IInboxRefresh
{
    void RequestRefresh();
}

/// <summary>
/// Fragt die Kataloge der Partner ab und gleicht den Eingang ab: neue Zustellungen erscheinen, verschwundene gelten
/// als zurückgezogen, abgelaufene als abgelaufen. Der eigene Connector filtert per Access-Policy; die Drive prüft
/// zusätzlich, dass Empfänger und Absender im Dataset zum eigenen Unternehmen und zum abgefragten Partner passen.
/// </summary>
public sealed class InboxPoller(
    IServiceScopeFactory scopes,
    IOptions<InboxOptions> options,
    TimeProvider clock,
    ILogger<InboxPoller> logger) : BackgroundService, IInboxRefresh
{
    private const int PageSize = 50;
    private const int MaxPages = 20;

    private readonly Channel<bool> _trigger = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void RequestRefresh() => _trigger.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!DeliveryWorker.WorkersEnabled(scopes))
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Eingang konnte nicht abgefragt werden");
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(options.Value.PollInterval);
            try
            {
                await _trigger.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Intervall abgelaufen
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Fragt alle empfangsbereiten Partner ab; liefert die Zahl neu gefundener Zustellungen.</summary>
    internal async Task<int> PollAllAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var edc = scope.ServiceProvider.GetRequiredService<EdcConnection>().Client;
        var ownBpn = scope.ServiceProvider.GetRequiredService<IOptions<ParticipantOptions>>().Value.Bpn;
        var now = clock.GetUtcNow().UtcDateTime;
        var found = 0;

        var partners = await db.Partners
            .Where(p => p.ReceivingEnabled && !p.Blocked && p.Did != null && p.ProtocolAddress != null && p.Protocol != null)
            .ToListAsync(ct);
        foreach (var partner in partners)
        {
            try
            {
                found += await PollPartnerAsync(db, edc, partner, ownBpn, now, ct);
                partner.LastPollError = null;
            }
            catch (Exception ex) when (ex is EdcException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                partner.LastPollError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                logger.LogWarning("Katalog von {Partner} ({Bpn}) nicht abrufbar: {Error}", partner.Name, partner.Bpn, ex.Message);
            }
            partner.LastPolledAt = now;
            await db.SaveChangesAsync(ct);
        }

        var expired = await db.IncomingOffers.Where(o => o.State == IncomingOfferState.New && o.ExpiresAt != null && o.ExpiresAt <= now).ToListAsync(ct);
        foreach (var offer in expired)
        {
            IncomingTransferWorker.MoveTo(offer, IncomingOfferState.Expired, now);
            offer.ConcurrencyStamp = Guid.NewGuid();
        }
        if (expired.Count > 0)
            await db.SaveChangesAsync(ct);
        return found;
    }

    private async Task<int> PollPartnerAsync(DriveDbContext db, EdcManagementClient edc, Partner partner, string ownBpn, DateTime now, CancellationToken ct)
    {
        var offers = new Dictionary<string, DeliveryOffer>();
        for (var page = 0; page < MaxPages; page++)
        {
            var catalog = await edc.RequestCatalogAsync(partner.ProtocolAddress!, partner.Did!, partner.Protocol!, page * PageSize, PageSize,
                DriveExchange.InboxFilters(ownBpn), ct);
            var datasets = JsonLd.Objects(JsonLd.Get(catalog, "dataset"));
            foreach (var dataset in datasets)
            {
                var offer = DriveExchange.ParseDataset(dataset);
                if (offer is null)
                    continue;
                if (offer.RecipientBpn != ownBpn || offer.SenderBpn != partner.Bpn)
                {
                    logger.LogWarning("Zustellung {AssetId} von {Partner} ignoriert: Empfänger {Recipient}, Absender {Sender} passen nicht",
                        offer.AssetId, partner.Bpn, offer.RecipientBpn, offer.SenderBpn);
                    continue;
                }
                offers[offer.AssetId] = offer;
            }
            if (datasets.Count < PageSize)
                break;
        }

        var existing = await db.IncomingOffers.Where(o => o.PartnerId == partner.Id).ToListAsync(ct);
        var created = 0;
        foreach (var offer in offers.Values)
        {
            var known = existing.FirstOrDefault(e => e.AssetId == offer.AssetId);
            if (known is not null)
            {
                known.LastSeenAt = now;
                known.OfferId = offer.OfferId;
                if (known.State == IncomingOfferState.Withdrawn)
                    IncomingTransferWorker.MoveTo(known, IncomingOfferState.New, now);
                continue;
            }
            var incoming = new IncomingOffer
            {
                Id = Guid.NewGuid(), PartnerId = partner.Id, ProviderBpn = partner.Bpn, AssetId = offer.AssetId, OfferId = offer.OfferId,
                Title = Limit(offer.Title, 200), Message = offer.Message is null ? null : Limit(offer.Message, 2000),
                SenderOrganization = offer.SenderOrganization is null ? null : Limit(offer.SenderOrganization, 200),
                ExpiresAt = offer.ExpiresAt?.UtcDateTime, TotalSize = offer.TotalSize, State = IncomingOfferState.New, StateChangedAt = now, FirstSeenAt = now,
                LastSeenAt = now,
            };
            db.IncomingOffers.Add(incoming);
            foreach (var item in offer.Items)
            {
                db.IncomingItems.Add(new IncomingItem
                {
                    Id = Guid.NewGuid(), OfferId = incoming.Id, ItemKey = Limit(item.Id, 20), Name = SafeName(item.Name), SizeBytes = item.Size,
                    Sha256 = item.Sha256.ToLowerInvariant(), ContentType = Limit(string.IsNullOrWhiteSpace(item.ContentType) ? "application/octet-stream" : item.ContentType, 255),
                    RelativePath = SafePath(item.Path),
                });
            }
            db.AuditEvents.Add(new AuditEvent
            {
                OccurredAt = now, Action = "inbox.offer-found", TargetType = "incomingOffer", TargetId = incoming.Id.ToString(),
                Details = $"{partner.Bpn}: {incoming.Title}, {offer.Items.Count} Datei(en), {offer.TotalSize} Bytes",
            });
            created++;
        }
        // Nicht mehr im Katalog: zurückgezogen (Widerruf oder Ablauf beim Absender).
        foreach (var offer in existing.Where(e => e.State == IncomingOfferState.New && !offers.ContainsKey(e.AssetId)))
            IncomingTransferWorker.MoveTo(offer, IncomingOfferState.Withdrawn, now);
        return created;
    }

    /// <summary>Dateinamen kommen vom Partner: unzulässige Zeichen ersetzen, Länge begrenzen.</summary>
    internal static string SafeName(string? name)
    {
        var chars = (name ?? "").Trim().Select(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim().TrimEnd('.');
        if (safe.Length == 0)
            safe = "Datei";
        return safe.Length > 255 ? safe[..255] : safe;
    }

    /// <summary>Ordnerpfad vom Partner: Segmente bereinigen, „.“/„..“ und leere verwerfen, Tiefe begrenzen.</summary>
    internal static string? SafePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var segments = path.Split('/', '\\')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s is not ("." or ".."))
            .Take(10)
            .Select(SafeName)
            .ToList();
        var joined = string.Join('/', segments);
        return joined.Length == 0 ? null : joined.Length > 1000 ? joined[..1000] : joined;
    }

    private static string Limit(string value, int max) => value.Length > max ? value[..max] : value;
}
