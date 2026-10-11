using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataspaceDrive.Infrastructure.Files;

/// <summary>
/// Räumt abgelaufene Uploads auf (z. B. Browser während des Uploads geschlossen); ihre Teilstücke würden sonst
/// dauerhaft das Volume belegen.
/// </summary>
public sealed class UploadCleanupWorker(
    IServiceScopeFactory scopes,
    IUploadStaging staging,
    TimeProvider clock,
    ILogger<UploadCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = StartDelay;
        while (true)
        {
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Abgelaufene Uploads konnten nicht aufgeräumt werden");
            }
            delay = Interval;
        }
    }

    /// <summary>Entfernt abgelaufene Upload-Sitzungen samt Teilstücken; liefert deren Anzahl.</summary>
    internal async Task<int> CleanupAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var expired = await db.UploadSessions.Where(u => u.ExpiresAt < now).ToListAsync(ct);
        foreach (var session in expired)
        {
            await staging.DiscardAsync(session.Id, ct);
            db.UploadSessions.Remove(session);
        }
        await db.SaveChangesAsync(ct);
        if (expired.Count > 0)
            logger.LogInformation("{Count} abgelaufene Uploads aufgeräumt", expired.Count);
        return expired.Count;
    }
}
