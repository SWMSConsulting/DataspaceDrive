using System.Security.Claims;
using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Users;

namespace DataspaceDrive.Api.Auth;

/// <summary>Angemeldeter Nutzer des aktuellen Requests; beim ersten Zugriff wird er (just in time) angelegt.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, IUserProvisioning provisioning, OidcSettings oidc)
{
    private (DriveUser User, DrivePrincipal Principal)? _resolved;

    public async Task<DrivePrincipal> PrincipalAsync(CancellationToken ct) => (await ResolveAsync(ct)).Principal;

    public async Task<DriveUser> UserAsync(CancellationToken ct) => (await ResolveAsync(ct)).User;

    private async Task<(DriveUser User, DrivePrincipal Principal)> ResolveAsync(CancellationToken ct)
    {
        if (_resolved is { } cached)
            return cached;
        var claims = accessor.HttpContext?.User ?? throw new AccessDeniedException("Nicht angemeldet.");
        var subject = claims.FindFirstValue("sub") ?? claims.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new AccessDeniedException("Die Anmeldung enthält keine Nutzerkennung.");
        var name = claims.FindFirstValue("name") ?? claims.FindFirstValue("preferred_username") ?? subject;
        var email = claims.FindFirstValue("email");

        var user = await provisioning.EnsureUserAsync(subject, name, email, ct);
        if (!user.IsActive)
            throw new AccessDeniedException("Dieses Konto ist deaktiviert.");
        var roles = claims.FindAll(oidc.RoleClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var groups = claims.FindAll(oidc.GroupClaim).Select(c => c.Value.TrimStart('/')).ToHashSet(StringComparer.Ordinal);
        await provisioning.RecordRolesAsync(user, roles, ct);
        var principal = new DrivePrincipal(user.Id, roles, groups);
        _resolved = (user, principal);
        return (user, principal);
    }
}
