using System.Net;
using DataspaceDrive.Core.Settings;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Infrastructure.Inbox;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swms.Edc.Client;
using Swms.Edc.Client.Policies;
using Swms.Edc.Client.Requests;

namespace DataspaceDrive.Infrastructure.Sharing;

/// <summary>
/// Benachrichtigung über den Dataspace. Jede Drive bietet ein Eingangs-Asset an, das nur ihre Partner verhandeln
/// dürfen. Nach einer neuen Zustellung ruft der Absender es ab; die Empfänger-Drive fragt daraufhin sofort den
/// Katalog ab. Der Abruf trägt kein Geheimnis und löst nur eine Katalogabfrage aus; fällt er aus, greift das Polling.
/// </summary>
public static class PartnerNotifications
{
    private const string AppliedKey = "inbox.applied";
    private static readonly TimeSpan PollStep = TimeSpan.FromSeconds(2);
    // Läuft in der Schleife der Zustellungen: ein hängender Partner darf die Anlage neuer Zustellungen nicht aufhalten.
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ein Schritt der Benachrichtigung; liefert den Zeitpunkt für den nächsten Schritt (Verhandlung läuft) oder null,
    /// wenn die Benachrichtigung erledigt ist oder entfällt.
    /// </summary>
    public static async Task<DateTime?> NotifyAsync(IServiceProvider services, DriveDbContext db, EdcManagementClient edc, Delivery delivery,
        DateTime now, CancellationToken ct)
    {
        var partner = await db.Partners.FirstAsync(p => p.Id == delivery.PartnerId, ct);
        if (!partner.IsResolved || partner.Blocked || delivery.State != DeliveryState.Active)
            return null;
        using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
        step.CancelAfter(StepTimeout);
        var remote = step.Token;
        if (partner.NotifyTransferProcessId is null)
        {
            if (partner.NotifyNegotiationId is null)
            {
                var catalog = await edc.RequestCatalogAsync(partner.ProtocolAddress!, partner.Did!, partner.Protocol!, 0, 10,
                    DriveExchange.InboxAssetFilters(), remote);
                var inbox = JsonLd.Objects(JsonLd.Get(catalog, "dataset")).Select(DriveExchange.ParseInboxDataset).FirstOrDefault(x => x is not null);
                if (inbox is not { } found)
                {
                    partner.NotifyError = "Der Partner bietet kein Eingangs-Asset an (ältere Drive oder kein Partnereintrag).";
                    return null;
                }
                partner.NotifyAssetId = found.AssetId;
                partner.NotifyNegotiationId = await edc.StartEdrNegotiationAsync(EdcRequests.ContractRequest(found.OfferId, partner.Did!, found.AssetId,
                    partner.ProtocolAddress!, partner.Protocol!, ExchangeProfile.MembershipContractPermissions()), remote);
                return now + PollStep;
            }
            var negotiation = await edc.GetNegotiationAsync(partner.NotifyNegotiationId, remote);
            if (negotiation.IsTerminated)
            {
                partner.NotifyNegotiationId = null;
                partner.NotifyError = $"Verhandlung um das Eingangs-Asset abgelehnt{(negotiation.ErrorDetail is { } d ? ": " + d : "")}";
                return null;
            }
            if (!negotiation.IsFinalized)
                return now + PollStep;
            var transferProcessId = await edc.FindEdrTransferProcessAsync(partner.NotifyNegotiationId, remote);
            if (transferProcessId is null)
                return now + PollStep;
            partner.NotifyTransferProcessId = transferProcessId;
        }

        var address = await edc.GetEdrDataAddressAsync(partner.NotifyTransferProcessId, remote);
        if (address is null)
        {
            Reset(partner, "Kein EDR für das Eingangs-Asset; neue Verhandlung.");
            return now + PollStep;
        }
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(IncomingTransferWorker.DataplaneClient);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{address.Endpoint.TrimEnd('/')}?event=delivery&asset={Uri.EscapeDataString(DriveExchange.AssetId(delivery.ExternalId))}");
        request.Headers.TryAddWithoutValidation("Authorization", address.Authorization);
        using var response = await http.SendAsync(request, remote);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            // Vertrag zurückgezogen oder Asset geändert: beim nächsten Schritt neu verhandeln.
            Reset(partner, $"Eingang des Partners antwortet {(int)response.StatusCode}; neue Verhandlung.");
            return now + PollStep;
        }
        response.EnsureSuccessStatusCode();
        partner.LastNotifiedAt = now;
        partner.NotifyError = null;
        return null;
    }

    /// <summary>
    /// Hält das eigene Eingangs-Asset aktuell: Access-Policy mit allen empfangsbereiten Partnern, Asset mit aktuellem Token.
    /// Nur wenn sich etwas geändert hat, spricht die Drive mit dem Connector.
    /// </summary>
    public static async Task<bool> EnsureInboxAsync(IServiceProvider services, DriveDbContext db, EdcManagementClient edc, DateTime now,
        CancellationToken ct)
    {
        var tokens = services.GetRequiredService<DeliveryTokenService>();
        var sharing = services.GetRequiredService<IOptions<SharingOptions>>().Value;
        var participant = services.GetRequiredService<IOptions<ParticipantOptions>>().Value;
        if (string.IsNullOrWhiteSpace(sharing.ContentBaseUrl) || string.IsNullOrWhiteSpace(participant.Bpn))
            return false;
        var bpns = await db.Partners.Where(p => p.ReceivingEnabled && !p.Blocked).Select(p => p.Bpn).OrderBy(b => b).ToListAsync(ct);
        var desired = $"{string.Join(',', bpns)}|v{tokens.CurrentVersion}|{sharing.ContentBaseUrl}";
        var applied = await db.Settings.FirstOrDefaultAsync(s => s.Key == AppliedKey, ct);
        if (applied?.Value == desired)
            return false;

        if (bpns.Count == 0)
        {
            await edc.DeleteContractDefinitionAsync(DriveExchange.InboxContractDefinitionId, ct);
        }
        else
        {
            var policy = DriveExchange.InboxAccessPolicy(bpns);
            if (await edc.CreatePolicyAsync(policy, ct) == CreateOutcome.AlreadyExists)
                await edc.UpdatePolicyAsync(policy, ct);
            await edc.CreatePolicyAsync(DriveExchange.ContractPolicy(), ct);
            var asset = DriveExchange.InboxAsset(participant.Bpn, sharing.ContentBaseUrl, tokens.Create(DriveExchange.InboxTokenSubject));
            if (await edc.CreateAssetAsync(asset, ct) == CreateOutcome.AlreadyExists)
                await edc.UpdateAssetAsync(asset, ct);
            await edc.CreateContractDefinitionAsync(DriveExchange.InboxContractDefinition(), ct);
        }
        if (applied is null)
            db.Settings.Add(new InstanceSetting { Key = AppliedKey, Value = desired, ChangedAt = now });
        else
        {
            applied.Value = desired;
            applied.ChangedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static void Reset(Core.Partners.Partner partner, string error)
    {
        partner.NotifyNegotiationId = null;
        partner.NotifyTransferProcessId = null;
        partner.NotifyError = error;
    }
}
