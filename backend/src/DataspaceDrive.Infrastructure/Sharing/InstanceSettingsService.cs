using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Settings;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Infrastructure.Sharing;

/// <summary>Einstellungen aus der Datenbank; ohne Eintrag gilt die Konfiguration (Helm-Wert).</summary>
public sealed class InstanceSettingsService(DriveDbContext db, IOptions<SharingOptions> options, TimeProvider clock) : IInstanceSettings
{
    private const string RequireApprovalKey = "sharing.requireApproval";

    public async Task<SharingSettings> SharingAsync(CancellationToken ct)
    {
        var stored = await db.Settings.Where(s => s.Key == RequireApprovalKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return new SharingSettings(stored is null ? options.Value.RequireApproval : stored == "true");
    }

    public async Task<SharingSettings> UpdateSharingAsync(DrivePrincipal admin, SharingSettings settings, CancellationToken ct)
    {
        if (!admin.IsAdmin)
            throw new AccessDeniedException("Einstellungen ändern nur Administratoren.");
        var now = clock.GetUtcNow().UtcDateTime;
        var value = settings.RequireApproval ? "true" : "false";
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == RequireApprovalKey, ct);
        if (row is null)
            db.Settings.Add(new InstanceSetting { Key = RequireApprovalKey, Value = value, ChangedAt = now, ChangedBy = admin.UserId });
        else
        {
            row.Value = value;
            row.ChangedAt = now;
            row.ChangedBy = admin.UserId;
        }
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = now, ActorUserId = admin.UserId, Action = "settings.changed", TargetType = "settings", TargetId = RequireApprovalKey,
            Details = $"Vier-Augen-Prinzip {(settings.RequireApproval ? "ein" : "aus")}",
        });
        await db.SaveChangesAsync(ct);
        return settings;
    }
}
