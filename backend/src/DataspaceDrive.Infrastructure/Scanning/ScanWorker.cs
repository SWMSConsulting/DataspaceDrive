using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Scanning;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataspaceDrive.Infrastructure.Scanning;

/// <summary>
/// Prüft wartende Dateiversionen nacheinander. Ist clamd nicht erreichbar, bleibt die Version auf
/// <see cref="ScanStatus.Pending"/> und damit gesperrt; der nächste Durchlauf versucht es erneut.
/// </summary>
public sealed class ScanWorker(
    IServiceScopeFactory scopes,
    IVirusScanner scanner,
    IBlobStore blobs,
    TimeProvider clock,
    ILogger<ScanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await ScanNextAsync(stoppingToken) ? TimeSpan.Zero : IdleDelay;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Virenscan fehlgeschlagen; neuer Versuch in {Delay}", ErrorDelay);
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

    /// <summary>Prüft die älteste wartende Version; false, wenn nichts zu tun war.</summary>
    internal async Task<bool> ScanNextAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var version = await db.FileVersions
            .Where(v => v.ScanStatus == ScanStatus.Pending)
            .OrderBy(v => v.UploadedAt)
            .FirstOrDefaultAsync(ct);
        if (version is null)
            return false;

        ScanResult result;
        await using (var content = await blobs.OpenReadAsync(version.BlobKey, ct))
            result = await scanner.ScanAsync(content, ct);

        version.ScanStatus = result.Verdict switch
        {
            ScanVerdict.Clean => ScanStatus.Clean,
            ScanVerdict.Infected => ScanStatus.Infected,
            ScanVerdict.NotScannable => ScanStatus.NotScannable,
            _ => ScanStatus.Error,
        };
        version.ScanDetail = result.Detail;
        version.ScannedAt = clock.GetUtcNow().UtcDateTime;
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = version.ScannedAt.Value,
            Action = "file.scanned",
            TargetType = "fileVersion",
            TargetId = version.Id.ToString(),
            Details = $"{version.ScanStatus}{(result.Detail is null ? "" : ": " + result.Detail)}",
        });
        await db.SaveChangesAsync(ct);
        if (version.ScanStatus != ScanStatus.Clean)
            logger.LogWarning("Version {VersionId} nach Virenscan gesperrt: {Status} {Detail}", version.Id, version.ScanStatus, result.Detail);
        return true;
    }
}
