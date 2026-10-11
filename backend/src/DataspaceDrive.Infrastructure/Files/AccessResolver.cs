using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataspaceDrive.Infrastructure.Files;

/// <summary>Lädt Bereiche samt ACL und wertet das Recht des angemeldeten Nutzers aus.</summary>
public sealed class AccessResolver(DriveDbContext db)
{
    public async Task<Permission> PermissionAsync(DrivePrincipal principal, Space space, CancellationToken ct)
    {
        if (space.Kind == SpaceKind.Personal)
            return SpaceAccess.Evaluate(space, [], principal);
        var acl = await db.AclEntries.Where(a => a.SpaceId == space.Id).ToListAsync(ct);
        return SpaceAccess.Evaluate(space, acl, principal);
    }

    /// <summary>
    /// Ohne jedes Recht meldet die Prüfung „nicht gefunden“ statt „verboten“, damit fremde Bereiche nicht
    /// über ihre IDs erkundet werden können.
    /// </summary>
    public async Task<Space> RequireAsync(DrivePrincipal principal, Guid spaceId, Permission needed, CancellationToken ct)
    {
        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, ct)
            ?? throw new NotFoundException("Der Bereich wurde nicht gefunden.");
        var permission = await PermissionAsync(principal, space, ct);
        if (permission == Permission.None)
            throw new NotFoundException("Der Bereich wurde nicht gefunden.");
        if (permission < needed)
            throw new AccessDeniedException("Für diese Aktion fehlt die Berechtigung.");
        return space;
    }

    public async Task<IReadOnlyList<(Space Space, Permission Permission)>> AccessibleSpacesAsync(DrivePrincipal principal,
        Permission minimum, CancellationToken ct)
    {
        var personal = await db.Spaces
            .Where(s => s.Kind == SpaceKind.Personal && s.OwnerUserId == principal.UserId)
            .ToListAsync(ct);
        var teams = await db.Spaces.Where(s => s.Kind == SpaceKind.Team).OrderBy(s => s.Name).ToListAsync(ct);
        var teamIds = teams.Select(t => t.Id).ToList();
        var acl = await db.AclEntries.Where(a => teamIds.Contains(a.SpaceId)).ToListAsync(ct);
        return personal.Select(s => (s, Permission.Manage))
            .Concat(teams.Select(t => (t, SpaceAccess.Evaluate(t, acl, principal))))
            .Where(x => x.Item2 >= minimum)
            .ToList();
    }
}
