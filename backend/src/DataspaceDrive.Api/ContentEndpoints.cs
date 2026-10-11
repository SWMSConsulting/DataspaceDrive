using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;

namespace DataspaceDrive.Api;

/// <summary>
/// Content-Endpunkt für die EDC-Dataplane. Die Dataplane ruft ihn beim Abruf eines Zustell-Assets auf und
/// schickt dabei das Zustell-Token (authKey/authCode der dataAddress) sowie bei HttpData-Assets die Header
/// <c>Edc-Bpn</c> und <c>Edc-Contract-Agreement-Id</c> mit. Er muss sofort streamen: die Dataplane bricht
/// nach 30 Sekunden ohne Daten ab.
/// </summary>
public static class ContentEndpoints
{
    public const string TokenHeader = "X-Drive-Delivery-Token";
    public const string ConsumerBpnHeader = "Edc-Bpn";
    public const string AgreementHeader = "Edc-Contract-Agreement-Id";

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/edc/deliveries/{deliveryId}", (string deliveryId, HttpContext context, ContentServices services,
            CancellationToken ct) => ServeAsync(deliveryId, null, context, services, ct));
        endpoints.MapGet("/edc/deliveries/{deliveryId}/items/{itemId}", (string deliveryId, string itemId, HttpContext context,
            ContentServices services, CancellationToken ct) => ServeAsync(deliveryId, itemId, context, services, ct));
        return endpoints;
    }

    private static async Task<IResult> ServeAsync(string deliveryId, string? itemId, HttpContext context, ContentServices services,
        CancellationToken ct)
    {
        var headers = context.Request.Headers;
        var tokenValid = services.Tokens.Verify(deliveryId, headers[TokenHeader].ToString());
        var delivery = tokenValid ? await services.Directory.FindDeliveryAsync(deliveryId, ct) : null;
        var agreementId = headers[AgreementHeader].ToString();
        var agreement = delivery is not null && agreementId.Length > 0
            ? await services.Directory.FindAgreementAsync(agreementId, ct)
            : null;
        var consumerBpn = headers[ConsumerBpnHeader].ToString();

        var decision = DeliveryAccess.Evaluate(tokenValid, delivery, consumerBpn, agreement, services.Clock.GetUtcNow());
        if (decision != DeliveryAccessDecision.Allowed)
        {
            services.Logger.LogWarning("Abruf von Zustellung {DeliveryId} abgelehnt: {Decision} (Edc-Bpn {ConsumerBpn}, Agreement {AgreementId})",
                deliveryId, decision, consumerBpn, agreementId);
            return Results.Problem(statusCode: StatusFor(decision), title: decision.ToString());
        }

        var item = await services.Directory.FindItemAsync(deliveryId, itemId, ct);
        if (item is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "ItemNotFound");

        services.Logger.LogInformation("Zustellung {DeliveryId}/{ItemId} wird an {ConsumerBpn} ausgeliefert (Agreement {AgreementId})",
            deliveryId, item.ItemId, consumerBpn, agreementId);
        var stream = await services.Blobs.OpenReadAsync(item.BlobKey, ct);
        return Results.Stream(stream, item.ContentType, item.FileName);
    }

    private static int StatusFor(DeliveryAccessDecision decision) => decision switch
    {
        DeliveryAccessDecision.InvalidToken => StatusCodes.Status401Unauthorized,
        DeliveryAccessDecision.UnknownDelivery => StatusCodes.Status404NotFound,
        DeliveryAccessDecision.NotActive or DeliveryAccessDecision.Expired => StatusCodes.Status410Gone,
        _ => StatusCodes.Status403Forbidden,
    };
}

/// <summary>Abhängigkeiten des Content-Endpunkts, gebündelt für schlanke Endpunkt-Signaturen.</summary>
public sealed class ContentServices(
    DeliveryTokenService tokens,
    IDeliveryDirectory directory,
    IBlobStore blobs,
    TimeProvider clock,
    ILogger<ContentServices> logger)
{
    public DeliveryTokenService Tokens { get; } = tokens;
    public IDeliveryDirectory Directory { get; } = directory;
    public IBlobStore Blobs { get; } = blobs;
    public TimeProvider Clock { get; } = clock;
    public ILogger Logger { get; } = logger;
}
