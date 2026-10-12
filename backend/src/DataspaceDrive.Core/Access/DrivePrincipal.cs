using DataspaceDrive.Core.Files;

namespace DataspaceDrive.Core.Access;

/// <summary>Angemeldeter Nutzer mit Rollen und Gruppen aus dem Identity Provider.</summary>
public sealed record DrivePrincipal(Guid UserId, IReadOnlySet<string> Roles, IReadOnlySet<string> Groups)
{
    public const string RoleUser = "drive-user";
    public const string RoleExternalShare = "drive-external-share";
    public const string RoleInbox = "drive-inbox";
    public const string RoleAdmin = "drive-admin";

    /// <summary>Prüft Freigaben anderer, wenn das Vier-Augen-Prinzip eingeschaltet ist.</summary>
    public const string RoleApprover = "drive-approver";

    public bool IsAdmin => Roles.Contains(RoleAdmin);
}

public static class SpaceAccess
{
    /// <summary>
    /// Effektives Recht auf einen Bereich. Persönliche Bereiche gehören allein ihrem Besitzer; auch
    /// Administratoren haben dort keinen Inhaltszugriff. Für Team-Bereiche gilt das stärkste passende ACL-Recht.
    /// </summary>
    public static Permission Evaluate(Space space, IEnumerable<AclEntry> acl, DrivePrincipal principal)
    {
        if (space.Kind == SpaceKind.Personal)
            return space.OwnerUserId == principal.UserId ? Permission.Manage : Permission.None;

        var best = Permission.None;
        foreach (var entry in acl.Where(e => e.SpaceId == space.Id))
        {
            var matches = entry.PrincipalType switch
            {
                PrincipalType.User => Guid.TryParse(entry.PrincipalKey, out var id) && id == principal.UserId,
                PrincipalType.Role => principal.Roles.Contains(entry.PrincipalKey),
                PrincipalType.Group => principal.Groups.Contains(entry.PrincipalKey),
                _ => false,
            };
            if (matches && entry.Permission > best)
                best = entry.Permission;
        }
        return best;
    }
}
