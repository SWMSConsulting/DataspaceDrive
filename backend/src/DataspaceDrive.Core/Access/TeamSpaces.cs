namespace DataspaceDrive.Core.Access;

/// <summary>ACL-Eintrag eines Team-Bereichs; <see cref="DisplayName"/> ist bei Nutzern der Anzeigename, sonst der Schlüssel.</summary>
public sealed record AclEntryInfo(PrincipalType PrincipalType, string PrincipalKey, Permission Permission, string DisplayName);

public sealed record TeamSpaceInfo(
    Guid Id,
    string Name,
    Guid RootFolderId,
    DateTime CreatedAt,
    IReadOnlyList<AclEntryInfo> Acl,
    int FileCount,
    long StoredBytes);

/// <summary>Für Nutzer ist <see cref="PrincipalKey"/> die Drive-Nutzer-ID, für Rollen und Gruppen der Name aus dem IdP.</summary>
public sealed record AclEntryRequest(PrincipalType PrincipalType, string PrincipalKey, Permission Permission);

public sealed record TeamSpaceRequest(string Name, IReadOnlyList<AclEntryRequest>? Acl);

/// <summary>Nutzer, die sich schon einmal angemeldet haben und daher in ACLs auswählbar sind.</summary>
public sealed record KnownUser(Guid Id, string DisplayName, string? Email, DateTime LastLoginAt, bool IsActive);

/// <summary>
/// Verwaltung der Team-Bereiche, nur für Administratoren. Administratoren sehen dabei Namen, Rechte und Belegung,
/// aber keine Inhalte; Inhaltszugriff gibt allein die ACL.
/// </summary>
public interface ITeamSpaceService
{
    Task<IReadOnlyList<TeamSpaceInfo>> ListAsync(DrivePrincipal admin, CancellationToken ct);
    Task<TeamSpaceInfo> CreateAsync(DrivePrincipal admin, TeamSpaceRequest request, CancellationToken ct);
    Task<TeamSpaceInfo> UpdateAsync(DrivePrincipal admin, Guid spaceId, TeamSpaceRequest request, CancellationToken ct);

    /// <summary>Löscht nur leere Bereiche (auch der Papierkorb muss leer sein).</summary>
    Task DeleteAsync(DrivePrincipal admin, Guid spaceId, CancellationToken ct);

    Task<IReadOnlyList<KnownUser>> KnownUsersAsync(DrivePrincipal admin, CancellationToken ct);
}
