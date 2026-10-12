using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;

namespace DataspaceDrive.Infrastructure.Files;

public sealed class TeamSpaceService(DriveDbContext db, TimeProvider clock) : ITeamSpaceService
{
    private const int MaxAclEntries = 100;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<TeamSpaceInfo>> ListAsync(DrivePrincipal admin, CancellationToken ct)
    {
        RequireAdmin(admin);
        var spaces = await db.Spaces.Where(s => s.Kind == SpaceKind.Team).OrderBy(s => s.Name).ToListAsync(ct);
        return await ToInfosAsync(spaces, ct);
    }

    public async Task<TeamSpaceInfo> CreateAsync(DrivePrincipal admin, TeamSpaceRequest request, CancellationToken ct)
    {
        RequireAdmin(admin);
        var name = await ValidateNameAsync(request.Name, null, ct);
        var acl = await ValidateAclAsync(request.Acl, ct);
        var now = Now;
        var space = new Space
        {
            Id = Guid.NewGuid(), Kind = SpaceKind.Team, Name = name, RootFolderId = Guid.NewGuid(), CreatedAt = now,
            ExternalSharingAllowed = request.ExternalSharingAllowed,
        };
        db.Spaces.Add(space);
        db.Folders.Add(new Folder
        {
            Id = space.RootFolderId, SpaceId = space.Id, Name = name, NormalizedName = ItemName.Normalize(name), CreatedAt = now,
            CreatedBy = admin.UserId, ModifiedAt = now,
        });
        db.AclEntries.AddRange(acl.Select(a => ToEntity(space.Id, a)));
        Audit(admin, "space.created", space.Id, Describe(name, acl, request.ExternalSharingAllowed));
        await db.SaveChangesAsync(ct);
        return (await ToInfosAsync([space], ct))[0];
    }

    public async Task<TeamSpaceInfo> UpdateAsync(DrivePrincipal admin, Guid spaceId, TeamSpaceRequest request, CancellationToken ct)
    {
        RequireAdmin(admin);
        var space = await FindAsync(spaceId, ct);
        var name = await ValidateNameAsync(request.Name, space.Id, ct);
        var acl = await ValidateAclAsync(request.Acl, ct);
        if (name != space.Name)
        {
            // Der Wurzelordner trägt den Bereichsnamen, damit Pfade (z. B. im Papierkorb) stimmen.
            var root = await db.Folders.FirstAsync(f => f.Id == space.RootFolderId, ct);
            space.Name = root.Name = name;
            root.NormalizedName = ItemName.Normalize(name);
            root.ModifiedAt = Now;
        }
        space.ExternalSharingAllowed = request.ExternalSharingAllowed;
        db.AclEntries.RemoveRange(await db.AclEntries.Where(a => a.SpaceId == space.Id).ToListAsync(ct));
        db.AclEntries.AddRange(acl.Select(a => ToEntity(space.Id, a)));
        Audit(admin, "space.updated", space.Id, Describe(name, acl, request.ExternalSharingAllowed));
        await db.SaveChangesAsync(ct);
        return (await ToInfosAsync([space], ct))[0];
    }

    public async Task DeleteAsync(DrivePrincipal admin, Guid spaceId, CancellationToken ct)
    {
        RequireAdmin(admin);
        var space = await FindAsync(spaceId, ct);
        if (await db.Files.AnyAsync(f => f.SpaceId == space.Id, ct)
            || await db.Folders.AnyAsync(f => f.SpaceId == space.Id && f.ParentId != null, ct))
            throw new ConflictException("Der Bereich enthält noch Dateien oder Ordner. Auch der Papierkorb muss dafür leer sein.");
        db.Folders.Remove(await db.Folders.FirstAsync(f => f.Id == space.RootFolderId, ct));
        db.Spaces.Remove(space);
        Audit(admin, "space.deleted", space.Id, space.Name);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<KnownUser>> KnownUsersAsync(DrivePrincipal admin, CancellationToken ct)
    {
        RequireAdmin(admin);
        return await db.Users.OrderBy(u => u.DisplayName)
            .Select(u => new KnownUser(u.Id, u.DisplayName, u.Email, u.LastLoginAt, u.IsActive))
            .ToListAsync(ct);
    }

    private static void RequireAdmin(DrivePrincipal principal)
    {
        if (!principal.IsAdmin)
            throw new AccessDeniedException("Team-Bereiche dürfen nur Administratoren verwalten.");
    }

    private async Task<Space> FindAsync(Guid spaceId, CancellationToken ct) =>
        await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId && s.Kind == SpaceKind.Team, ct)
        ?? throw new NotFoundException("Der Team-Bereich wurde nicht gefunden.");

    private async Task<string> ValidateNameAsync(string? requested, Guid? currentId, CancellationToken ct)
    {
        var name = ItemName.Validate(requested);
        var normalized = ItemName.Normalize(name);
        // Die Bereiche stehen nebeneinander in der Wurzel des Datei-Browsers.
        if (normalized == ItemName.Normalize(UserProvisioning.PersonalSpaceName))
            throw new InvalidInputException($"„{UserProvisioning.PersonalSpaceName}“ ist für den persönlichen Bereich reserviert.");
        var others = await db.Spaces.Where(s => s.Kind == SpaceKind.Team && s.Id != currentId).Select(s => s.Name).ToListAsync(ct);
        if (others.Any(o => ItemName.Normalize(o) == normalized))
            throw new ConflictException($"Es gibt bereits einen Team-Bereich „{name}“.");
        return name;
    }

    private async Task<IReadOnlyList<AclEntryRequest>> ValidateAclAsync(IReadOnlyList<AclEntryRequest>? requested, CancellationToken ct)
    {
        var entries = requested ?? [];
        if (entries.Count > MaxAclEntries)
            throw new InvalidInputException($"Ein Bereich kann höchstens {MaxAclEntries} Berechtigungen haben.");
        var result = new List<AclEntryRequest>();
        foreach (var entry in entries)
        {
            if (!Enum.IsDefined(entry.PrincipalType))
                throw new InvalidInputException("Unbekannte Art der Berechtigung.");
            if (entry.Permission is not (Permission.Read or Permission.Write))
                throw new InvalidInputException("Für Team-Bereiche gibt es die Rechte Lesen und Schreiben.");
            var key = (entry.PrincipalKey ?? "").Trim();
            if (key.Length is 0 or > 255)
                throw new InvalidInputException("Jede Berechtigung braucht einen Nutzer, eine Rolle oder eine Gruppe.");
            if (entry.PrincipalType == PrincipalType.User)
            {
                if (!Guid.TryParse(key, out var userId) || !await db.Users.AnyAsync(u => u.Id == userId, ct))
                    throw new InvalidInputException("Der Nutzer ist unbekannt; Nutzer erscheinen nach ihrer ersten Anmeldung.");
                key = userId.ToString();
            }
            if (result.Any(r => r.PrincipalType == entry.PrincipalType && r.PrincipalKey == key))
                throw new InvalidInputException($"„{key}“ ist mehrfach eingetragen.");
            result.Add(entry with { PrincipalKey = key });
        }
        return result;
    }

    private async Task<IReadOnlyList<TeamSpaceInfo>> ToInfosAsync(IReadOnlyList<Space> spaces, CancellationToken ct)
    {
        var ids = spaces.Select(s => s.Id).ToList();
        var acl = await db.AclEntries.Where(a => ids.Contains(a.SpaceId)).ToListAsync(ct);
        var userIds = acl.Where(a => a.PrincipalType == PrincipalType.User).Select(a => Guid.Parse(a.PrincipalKey)).Distinct().ToList();
        var userNames = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id.ToString(), u => u.DisplayName, ct);
        var fileCounts = await db.Files.Where(f => ids.Contains(f.SpaceId) && f.DeletedAt == null)
            .GroupBy(f => f.SpaceId)
            .Select(g => new { SpaceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SpaceId, x => x.Count, ct);
        // Belegung inklusive aller Versionen und des Papierkorbs, also das, was tatsächlich Speicher kostet.
        var storedBytes = await (
                from v in db.FileVersions
                join f in db.Files on v.FileEntryId equals f.Id
                where ids.Contains(f.SpaceId)
                group v.SizeBytes by f.SpaceId into g
                select new { SpaceId = g.Key, Bytes = g.Sum() })
            .ToDictionaryAsync(x => x.SpaceId, x => x.Bytes, ct);
        return spaces.Select(s => new TeamSpaceInfo(
                s.Id, s.Name, s.RootFolderId, s.CreatedAt,
                acl.Where(a => a.SpaceId == s.Id)
                    .OrderBy(a => a.PrincipalType).ThenBy(a => a.PrincipalKey)
                    .Select(a => new AclEntryInfo(a.PrincipalType, a.PrincipalKey, a.Permission,
                        a.PrincipalType == PrincipalType.User ? userNames.GetValueOrDefault(a.PrincipalKey, "Unbekannter Nutzer") : a.PrincipalKey))
                    .ToList(),
                fileCounts.GetValueOrDefault(s.Id),
                storedBytes.GetValueOrDefault(s.Id),
                s.ExternalSharingAllowed))
            .ToList();
    }

    private static AclEntry ToEntity(Guid spaceId, AclEntryRequest entry) => new()
    {
        Id = Guid.NewGuid(), SpaceId = spaceId, PrincipalType = entry.PrincipalType, PrincipalKey = entry.PrincipalKey,
        Permission = entry.Permission,
    };

    private static string Describe(string name, IReadOnlyList<AclEntryRequest> acl, bool external) =>
        $"{name}: " + (acl.Count == 0 ? "keine Berechtigungen" : string.Join(", ", acl.Select(a => $"{a.PrincipalType} {a.PrincipalKey}={a.Permission}")))
        + (external ? "; externes Teilen erlaubt" : "");

    private void Audit(DrivePrincipal principal, string action, Guid spaceId, string details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = Now, ActorUserId = principal.UserId, Action = action, TargetType = "space", TargetId = spaceId.ToString(),
            Details = details,
        });
}
