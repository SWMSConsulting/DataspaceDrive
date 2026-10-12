using System.Buffers;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Inbox;
using Swms.Edc.Client.Policies;

namespace DataspaceDrive.Api;

/// <summary>
/// Content-Endpunkt für die EDC-Dataplane. Die Dataplane ruft ihn beim Abruf eines Zustell-Assets auf und
/// schickt dabei das Zustell-Token (authKey/authCode der dataAddress) sowie bei HttpData-Assets die Header
/// <c>Edc-Bpn</c> und <c>Edc-Contract-Agreement-Id</c> mit. Er muss sofort streamen: die Dataplane bricht
/// nach 30 Sekunden ohne Daten ab. Mit <c>?offset=&amp;length=</c> liefert er einen Abschnitt (Status 200).
/// </summary>
public static class ContentEndpoints
{
    public const string TokenHeader = "X-Drive-Delivery-Token";
    public const string ConsumerBpnHeader = "Edc-Bpn";
    public const string AgreementHeader = "Edc-Contract-Agreement-Id";

    private const int BufferSize = 81920;

    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/edc/deliveries/{deliveryId}", (string deliveryId, long? offset, long? length, HttpContext context,
            ContentServices services, CancellationToken ct) => ServeAsync(deliveryId, null, offset, length, context, services, ct));
        endpoints.MapGet("/edc/deliveries/{deliveryId}/items/{itemId}", (string deliveryId, string itemId, long? offset, long? length,
            HttpContext context, ContentServices services, CancellationToken ct) => ServeAsync(deliveryId, itemId, offset, length, context, services, ct));
        // Eingangs-Asset: ein Partner meldet eine neue Zustellung; das löst nur eine Katalogabfrage aus.
        endpoints.MapGet("/edc/notifications", async (string? asset, HttpContext context, ContentServices services, NotificationReceiver receiver,
            CancellationToken ct) =>
        {
            var headers = context.Request.Headers;
            if (!services.Tokens.Verify(DriveExchange.InboxTokenSubject, headers[TokenHeader].ToString()))
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "InvalidToken");
            var consumerBpn = headers[ConsumerBpnHeader].ToString();
            return consumerBpn.Length > 0 && await receiver.ReceivedAsync(consumerBpn, asset, ct)
                ? Results.NoContent()
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "UnknownPartner");
        });
        return endpoints;
    }

    private static async Task<IResult> ServeAsync(string deliveryId, string? itemId, long? offset, long? length, HttpContext context,
        ContentServices services, CancellationToken ct)
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
            if (tokenValid)
                await services.Directory.RecordRetrievalAsync(deliveryId, decision, consumerBpn, agreementId, itemId, offset, length, ct);
            return Results.Problem(statusCode: StatusFor(decision), title: decision.ToString());
        }

        var item = await services.Directory.FindItemAsync(deliveryId, itemId, ct);
        if (item is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "ItemNotFound");
        var start = offset ?? 0;
        if (start < 0 || length < 0 || start > item.Length)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "InvalidRange");
        var count = Math.Min(length ?? item.Length - start, item.Length - start);

        await services.Directory.RecordRetrievalAsync(deliveryId, decision, consumerBpn, agreementId, item.ItemId, offset, length, ct);
        services.Logger.LogInformation("Zustellung {DeliveryId}/{ItemId} ab {Offset} ({Count} Bytes) an {ConsumerBpn} (Agreement {AgreementId})",
            deliveryId, item.ItemId, start, count, consumerBpn, agreementId);
        var stream = await services.Blobs.OpenReadAsync(item.BlobKey, ct);
        return new SegmentResult(stream, start, count, item.ContentType, item.FileName);
    }

    private static int StatusFor(DeliveryAccessDecision decision) => decision switch
    {
        DeliveryAccessDecision.InvalidToken => StatusCodes.Status401Unauthorized,
        DeliveryAccessDecision.UnknownDelivery => StatusCodes.Status404NotFound,
        DeliveryAccessDecision.NotActive or DeliveryAccessDecision.Expired => StatusCodes.Status410Gone,
        _ => StatusCodes.Status403Forbidden,
    };

    /// <summary>Schreibt genau <c>count</c> Bytes ab <c>start</c>, mit Content-Length, damit die Gegenseite die Größe kennt.</summary>
    private sealed class SegmentResult(Stream stream, long start, long count, string contentType, string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            await using (stream)
            {
                if (start > 0)
                    stream.Seek(start, SeekOrigin.Begin);
                var response = context.Response;
                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = contentType;
                response.ContentLength = count;
                response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = fileName, DispositionType = "attachment" }.ToString();
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    var remaining = count;
                    while (remaining > 0)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, remaining)), context.RequestAborted);
                        if (read == 0)
                            break;
                        await response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                        remaining -= read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }
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
