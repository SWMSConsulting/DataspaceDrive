using DataspaceDrive.Api.Auth;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Partners;
using DataspaceDrive.Core.Shares;

namespace DataspaceDrive.Api;

public sealed record AcceptOfferRequest(Guid FolderId);

/// <summary>Freigaben an Partnerunternehmen, Eingang und Partnerverwaltung.</summary>
public static class SharingEndpoints
{
    public static IEndpointRouteBuilder MapSharingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api").RequireAuthorization(AuthSetup.UserPolicy);

        api.MapGet("/partners/targets", async (CurrentUser current, IPartnerService partners, CancellationToken ct) =>
            await partners.ShareTargetsAsync(await current.PrincipalAsync(ct), ct));

        api.MapPost("/shares", async (CreateShareRequest request, CurrentUser current, IShareService shares, CancellationToken ct) =>
        {
            var share = await shares.CreateAsync(await current.PrincipalAsync(ct), request, ct);
            return Results.Created($"/api/shares/{share.Id}", share);
        });
        api.MapGet("/shares", async (bool? all, CurrentUser current, IShareService shares, CancellationToken ct) =>
            await shares.ListAsync(await current.PrincipalAsync(ct), all == true, ct));
        api.MapPost("/shares/{shareId:guid}/revoke", async (Guid shareId, CurrentUser current, IShareService shares, CancellationToken ct) =>
            await shares.RevokeShareAsync(await current.PrincipalAsync(ct), shareId, ct));
        api.MapPost("/deliveries/{deliveryId:guid}/revoke", async (Guid deliveryId, CurrentUser current, IShareService shares, CancellationToken ct) =>
            await shares.RevokeDeliveryAsync(await current.PrincipalAsync(ct), deliveryId, ct));
        api.MapPost("/deliveries/{deliveryId:guid}/retry", async (Guid deliveryId, CurrentUser current, IShareService shares, CancellationToken ct) =>
            await shares.RetryDeliveryAsync(await current.PrincipalAsync(ct), deliveryId, ct));
        api.MapGet("/deliveries/{deliveryId:guid}/retrievals", async (Guid deliveryId, CurrentUser current, IShareService shares, CancellationToken ct) =>
            await shares.RetrievalsAsync(await current.PrincipalAsync(ct), deliveryId, ct));

        api.MapGet("/inbox", async (CurrentUser current, IInboxService inbox, CancellationToken ct) =>
            await inbox.ListAsync(await current.PrincipalAsync(ct), ct));
        api.MapPost("/inbox/refresh", async (CurrentUser current, IInboxService inbox, CancellationToken ct) =>
        {
            await inbox.RequestRefreshAsync(await current.PrincipalAsync(ct), ct);
            return Results.Accepted();
        });
        api.MapPost("/inbox/{offerId:guid}/accept", async (Guid offerId, AcceptOfferRequest request, CurrentUser current, IInboxService inbox,
            CancellationToken ct) => await inbox.AcceptAsync(await current.PrincipalAsync(ct), offerId, request.FolderId, ct));
        api.MapPost("/inbox/{offerId:guid}/decline", async (Guid offerId, CurrentUser current, IInboxService inbox, CancellationToken ct) =>
            await inbox.DeclineAsync(await current.PrincipalAsync(ct), offerId, ct));
        api.MapPost("/inbox/{offerId:guid}/retry", async (Guid offerId, CurrentUser current, IInboxService inbox, CancellationToken ct) =>
            await inbox.RetryAsync(await current.PrincipalAsync(ct), offerId, ct));

        var admin = endpoints.MapGroup("/api/admin/partners").RequireAuthorization(AuthSetup.AdminPolicy);
        admin.MapGet("", async (CurrentUser current, IPartnerService partners, CancellationToken ct) =>
            await partners.ListAsync(await current.PrincipalAsync(ct), ct));
        admin.MapPost("", async (PartnerRequest request, CurrentUser current, IPartnerService partners, CancellationToken ct) =>
        {
            var partner = await partners.CreateAsync(await current.PrincipalAsync(ct), request, ct);
            return Results.Created($"/api/admin/partners/{partner.Id}", partner);
        });
        admin.MapPut("/{partnerId:guid}", async (Guid partnerId, PartnerRequest request, CurrentUser current, IPartnerService partners,
            CancellationToken ct) => await partners.UpdateAsync(await current.PrincipalAsync(ct), partnerId, request, ct));
        admin.MapDelete("/{partnerId:guid}", async (Guid partnerId, CurrentUser current, IPartnerService partners, CancellationToken ct) =>
        {
            await partners.DeleteAsync(await current.PrincipalAsync(ct), partnerId, ct);
            return Results.NoContent();
        });
        admin.MapPost("/{partnerId:guid}/resolve", async (Guid partnerId, CurrentUser current, IPartnerService partners, CancellationToken ct) =>
            await partners.ResolveAsync(await current.PrincipalAsync(ct), partnerId, ct));

        return endpoints;
    }
}
