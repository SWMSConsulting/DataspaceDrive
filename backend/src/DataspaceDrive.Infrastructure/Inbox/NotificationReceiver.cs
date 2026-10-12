using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataspaceDrive.Infrastructure.Inbox;

/// <summary>Nimmt Benachrichtigungen von Partnern an (über das eigene Eingangs-Asset) und stößt die Katalogabfrage an.</summary>
public sealed class NotificationReceiver(DriveDbContext db, IServiceProvider services, TimeProvider clock, ILogger<NotificationReceiver> logger)
{
    /// <summary>False, wenn der Absender kein empfangsbereiter Partner ist.</summary>
    public async Task<bool> ReceivedAsync(string consumerBpn, string? assetId, CancellationToken ct)
    {
        var partner = await db.Partners.FirstOrDefaultAsync(p => p.Bpn == consumerBpn && p.ReceivingEnabled && !p.Blocked, ct);
        if (partner is null)
            return false;
        partner.LastNotificationReceivedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Benachrichtigung von {Partner} ({Bpn}) zu {AssetId}; Eingang wird abgefragt", partner.Name, partner.Bpn, assetId);
        services.GetService<IInboxRefresh>()?.RequestRefresh();
        return true;
    }
}
