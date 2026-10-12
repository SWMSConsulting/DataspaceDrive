using DataspaceDrive.Api.Auth;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Infrastructure.Sharing;

namespace DataspaceDrive.Api;

/// <summary>Verwaltung für Administratoren (Rolle drive-admin): Team-Bereiche und bekannte Nutzer.</summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin").RequireAuthorization(AuthSetup.AdminPolicy);

        admin.MapGet("/spaces", async (CurrentUser current, ITeamSpaceService spaces, CancellationToken ct) =>
            await spaces.ListAsync(await current.PrincipalAsync(ct), ct));

        admin.MapPost("/spaces", async (TeamSpaceRequest request, CurrentUser current, ITeamSpaceService spaces, CancellationToken ct) =>
        {
            var created = await spaces.CreateAsync(await current.PrincipalAsync(ct), request, ct);
            return Results.Created($"/api/admin/spaces/{created.Id}", created);
        });

        admin.MapPut("/spaces/{spaceId:guid}", async (Guid spaceId, TeamSpaceRequest request, CurrentUser current, ITeamSpaceService spaces,
            CancellationToken ct) => await spaces.UpdateAsync(await current.PrincipalAsync(ct), spaceId, request, ct));

        admin.MapDelete("/spaces/{spaceId:guid}", async (Guid spaceId, CurrentUser current, ITeamSpaceService spaces, CancellationToken ct) =>
        {
            await spaces.DeleteAsync(await current.PrincipalAsync(ct), spaceId, ct);
            return Results.NoContent();
        });

        admin.MapGet("/users", async (CurrentUser current, ITeamSpaceService spaces, CancellationToken ct) =>
            await spaces.KnownUsersAsync(await current.PrincipalAsync(ct), ct));

        admin.MapGet("/audit", async (int? take, CurrentUser current, IAuditService audit, CancellationToken ct) =>
            await audit.RecentAsync(await current.PrincipalAsync(ct), take ?? 500, ct));

        // Abgleich mit dem eigenen Connector: offene Contract Definitions, verwaiste und fehlende Zustellungs-Objekte.
        admin.MapGet("/edc", (EdcReconciliation reconciliation) => reconciliation.Status);
        admin.MapPost("/edc/check", async (EdcReconciliation reconciliation, CancellationToken ct) => await reconciliation.RunAsync(ct));

        return endpoints;
    }
}
