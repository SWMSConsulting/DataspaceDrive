using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Users;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataspaceDrive.Infrastructure.Users;

public sealed class UserProvisioning(DriveDbContext db, TimeProvider clock) : IUserProvisioning
{
    public const string PersonalSpaceName = "Meine Dateien";

    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    public async Task<DriveUser> EnsureUserAsync(string subject, string displayName, string? email, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject, ct);
        if (user is null)
            return await CreateAsync(subject, displayName, email, now, ct);

        // Nur schreiben, wenn sich etwas geändert hat; sonst würde jeder Request die Datenbank belasten.
        if (user.DisplayName != displayName || user.Email != email || now - user.LastLoginAt > LastSeenResolution)
        {
            user.DisplayName = displayName;
            user.Email = email;
            user.LastLoginAt = now;
            await db.SaveChangesAsync(ct);
        }
        return user;
    }

    public async Task RecordRolesAsync(DriveUser user, IReadOnlyCollection<string> roles, CancellationToken ct)
    {
        var marker = roles.Count == 0 ? null : "," + string.Join(',', roles.Order(StringComparer.Ordinal)) + ",";
        if (marker is { Length: > 1000 })
            marker = marker[..1000];
        if (user.Roles == marker)
            return;
        user.Roles = marker;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEmailNotificationsAsync(DriveUser user, bool enabled, CancellationToken ct)
    {
        user.EmailNotifications = enabled;
        await db.SaveChangesAsync(ct);
    }

    private async Task<DriveUser> CreateAsync(string subject, string displayName, string? email, DateTime now, CancellationToken ct)
    {
        var user = new DriveUser
        {
            Id = Guid.NewGuid(), Subject = subject, DisplayName = displayName, Email = email, CreatedAt = now, LastLoginAt = now,
        };
        var spaceId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        db.Users.Add(user);
        db.Spaces.Add(new Space
        {
            Id = spaceId, Kind = SpaceKind.Personal, Name = PersonalSpaceName, OwnerUserId = user.Id, RootFolderId = rootId,
            CreatedAt = now,
        });
        db.Folders.Add(new Folder
        {
            Id = rootId, SpaceId = spaceId, Name = PersonalSpaceName, NormalizedName = ItemName.Normalize(PersonalSpaceName),
            CreatedAt = now, CreatedBy = user.Id, ModifiedAt = now,
        });
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = now, ActorUserId = user.Id, Action = "user.created", TargetType = "user", TargetId = user.Id.ToString(),
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return user;
        }
        catch (DbUpdateException)
        {
            // Zwei gleichzeitige erste Requests desselben Nutzers: der andere war schneller.
            db.ChangeTracker.Clear();
            return await db.Users.FirstAsync(u => u.Subject == subject, ct);
        }
    }
}
