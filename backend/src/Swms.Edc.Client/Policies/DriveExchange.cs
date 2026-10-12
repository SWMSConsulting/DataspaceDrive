using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Swms.Edc.Client.Requests;

namespace Swms.Edc.Client.Policies;

/// <summary>
/// Datei einer Zustellung, wie sie im Manifest des Assets steht. <paramref name="Path"/> ist bei Ordnerfreigaben der
/// relative Ordnerpfad („Begehung/Fotos“, Trenner „/“); Empfänger ohne Ordnerunterstützung legen die Datei flach ab.
/// </summary>
/// <param name="Source">Stabile Kennung der Quelldatei über Revisionen hinweg; der Empfänger hängt neue Revisionen daran als Version an.</param>
public sealed record ManifestItem(string Id, string Name, long Size, string Sha256, string ContentType, string? Path = null, string? Source = null);

public sealed record DeliveryAssetSpec(
    string DeliveryId,
    string InstanceId,
    string Title,
    string? Message,
    string RecipientBpn,
    string SenderBpn,
    string? SenderOrganization,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<ManifestItem> Items,
    string ContentBaseUrl,
    string AuthCode,
    string? LineageId = null,
    int Revision = 1);

/// <summary>Zustellung aus dem Katalog eines Partners, wie sie der Empfänger liest.</summary>
public sealed record DeliveryOffer(
    string AssetId,
    string OfferId,
    string Title,
    string? Message,
    string RecipientBpn,
    string SenderBpn,
    string? SenderOrganization,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<ManifestItem> Items,
    string? LineageId = null,
    int Revision = 1)
{
    public long TotalSize => Items.Sum(i => i.Size);
}

/// <summary>
/// Austauschprofil v1 zwischen zwei Drive-Instanzen: IDs, Asset-Properties, Manifest und Katalogfilter. Beide
/// Seiten müssen dieselbe Version kennen; Änderungen gehen nur über eine neue <see cref="SchemaVersion"/>.
/// </summary>
public static class DriveExchange
{
    public const string Namespace = "https://ns.swms-cloud.com/drive/v1/";
    public const string Kind = "delivery";
    public const string SchemaVersion = "1";
    public const string AuthHeader = "X-Drive-Delivery-Token";
    public const string MembershipContractPolicyId = "drive-cp-membership-v1";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        // ASCII-escaped, damit Umlaute in Dateinamen jede Zwischenschicht unbeschadet überstehen.
        Encoder = JavaScriptEncoder.Default,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Eingangs-Asset: Partner rufen es nach einer neuen Zustellung ab, damit der Eingang sofort nachsieht.</summary>
    public const string InboxKind = "inbox";
    public const string InboxAssetId = "drive-inbox-v1";
    public const string InboxContractDefinitionId = "drive-cd-inbox-v1";
    public const string InboxAccessPolicyId = "drive-ap-inbox-v1";
    public const string InboxTokenSubject = "inbox";

    public static string AssetId(string deliveryId) => $"drive-d-{deliveryId}";

    /// <summary>Eingangs-Asset: HttpData auf den Content-Endpunkt /edc/notifications, Query-Parameter werden durchgereicht.</summary>
    public static JsonObject InboxAsset(string ownBpn, string contentBaseUrl, string authCode) => new()
    {
        ["@context"] = new JsonObject { ["@vocab"] = EdcNamespaces.Edc, ["dct"] = EdcNamespaces.Dct, ["drive"] = Namespace },
        ["@type"] = "Asset",
        ["@id"] = InboxAssetId,
        ["properties"] = new JsonObject
        {
            ["name"] = "DataspaceDrive-Eingang",
            ["dct:title"] = "DataspaceDrive-Eingang",
            ["drive:kind"] = InboxKind,
            ["drive:schemaVersion"] = SchemaVersion,
            ["drive:ownerBpn"] = ownBpn,
        },
        ["dataAddress"] = new JsonObject
        {
            ["@type"] = "DataAddress",
            ["type"] = "HttpData",
            ["baseUrl"] = $"{contentBaseUrl.TrimEnd('/')}/edc/notifications",
            ["proxyPath"] = "false",
            ["proxyQueryParams"] = "true",
            ["proxyMethod"] = "false",
            ["proxyBody"] = "false",
            ["authKey"] = AuthHeader,
            ["authCode"] = authCode,
        },
    };

    public static JsonObject InboxAccessPolicy(IEnumerable<string> bpns) => ExchangeProfile.AccessPolicy(InboxAccessPolicyId, bpns);

    public static JsonObject InboxContractDefinition() =>
        EdcRequests.ContractDefinition(InboxContractDefinitionId, InboxAccessPolicyId, MembershipContractPolicyId, InboxAssetId);

    public static IReadOnlyList<Criterion> InboxAssetFilters() => [new Criterion(Namespace + "kind", "=", InboxKind)];

    /// <summary>Eingangs-Asset eines Partners aus seinem Katalog: Asset- und Offer-ID.</summary>
    public static (string AssetId, string OfferId)? ParseInboxDataset(JsonObject dataset)
    {
        var assetId = JsonLd.Id(dataset);
        var offerId = JsonLd.Objects(JsonLd.Get(dataset, "hasPolicy")).Select(JsonLd.Id).FirstOrDefault(id => id is not null);
        return assetId is null || offerId is null || Drive(dataset, "kind") != InboxKind ? null : (assetId, offerId);
    }
    public static string ContractDefinitionId(string deliveryId) => $"drive-cd-{deliveryId}";
    public static string AccessPolicyId(string bpn) => $"drive-ap-{bpn}";

    public static JsonObject DeliveryAsset(DeliveryAssetSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.DeliveryId);
        if (spec.Items.Count == 0)
            throw new ArgumentException("Eine Zustellung braucht mindestens eine Datei.", nameof(spec));
        var properties = new JsonObject
        {
            ["name"] = spec.Title,
            ["dct:title"] = spec.Title,
            ["contenttype"] = spec.Items.Count == 1 ? spec.Items[0].ContentType : "application/octet-stream",
            ["drive:kind"] = Kind,
            ["drive:schemaVersion"] = SchemaVersion,
            ["drive:recipientBpn"] = spec.RecipientBpn,
            ["drive:senderBpn"] = spec.SenderBpn,
            ["drive:expiresAt"] = spec.ExpiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["drive:itemCount"] = spec.Items.Count.ToString(CultureInfo.InvariantCulture),
            ["drive:totalSize"] = spec.Items.Sum(i => i.Size).ToString(CultureInfo.InvariantCulture),
            ["drive:manifest"] = JsonSerializer.Serialize(spec.Items, ManifestJson),
        };
        if (!string.IsNullOrWhiteSpace(spec.Message))
            properties["drive:message"] = spec.Message;
        if (!string.IsNullOrWhiteSpace(spec.SenderOrganization))
            properties["drive:senderOrganization"] = spec.SenderOrganization;
        // Folgefreigaben derselben Auswahl teilen die Linie; die Revision zählt hoch.
        if (!string.IsNullOrWhiteSpace(spec.LineageId))
        {
            properties["drive:lineageId"] = spec.LineageId;
            properties["drive:revision"] = spec.Revision.ToString(CultureInfo.InvariantCulture);
        }
        return new JsonObject
        {
            ["@context"] = new JsonObject { ["@vocab"] = EdcNamespaces.Edc, ["dct"] = EdcNamespaces.Dct, ["drive"] = Namespace },
            ["@type"] = "Asset",
            ["@id"] = AssetId(spec.DeliveryId),
            ["properties"] = properties,
            ["privateProperties"] = new JsonObject { ["drive:deliveryId"] = spec.DeliveryId, ["drive:instanceId"] = spec.InstanceId },
            ["dataAddress"] = new JsonObject
            {
                ["@type"] = "DataAddress",
                // HttpData, weil nur dieser Typ unter 0.12 Edc-Bpn und Edc-Contract-Agreement-Id mitschickt.
                ["type"] = "HttpData",
                ["baseUrl"] = $"{spec.ContentBaseUrl.TrimEnd('/')}/edc/deliveries/{spec.DeliveryId}",
                ["proxyPath"] = "true",
                ["proxyQueryParams"] = "true",
                ["proxyMethod"] = "false",
                ["proxyBody"] = "false",
                ["authKey"] = AuthHeader,
                ["authCode"] = spec.AuthCode,
            },
        };
    }

    /// <summary>Contract Definition genau dieser Zustellung: Access-Policy des Empfängers, Profil-Contract-Policy.</summary>
    public static JsonObject DeliveryContractDefinition(string deliveryId, string recipientBpn) =>
        EdcRequests.ContractDefinition(ContractDefinitionId(deliveryId), AccessPolicyId(recipientBpn), MembershipContractPolicyId,
            AssetId(deliveryId));

    public static JsonObject AccessPolicy(string recipientBpn) => ExchangeProfile.AccessPolicy(AccessPolicyId(recipientBpn), [recipientBpn]);

    public static JsonObject ContractPolicy() =>
        ExchangeProfile.PolicyDefinition(MembershipContractPolicyId, ExchangeProfile.MembershipContractPermissions());

    /// <summary>Katalogfilter des Empfängers: nur Zustellungen, nur an die eigene BPN.</summary>
    public static IReadOnlyList<Criterion> InboxFilters(string ownBpn) =>
        [new Criterion(Namespace + "kind", "=", Kind), new Criterion(Namespace + "recipientBpn", "=", ownBpn)];

    /// <summary>Liest eine Zustellung aus einem Katalog-Dataset; null, wenn es keine gültige Zustellung dieses Profils ist.</summary>
    public static DeliveryOffer? ParseDataset(JsonObject dataset)
    {
        var assetId = JsonLd.Id(dataset);
        var offerId = JsonLd.Objects(JsonLd.Get(dataset, "hasPolicy")).Select(JsonLd.Id).FirstOrDefault(id => id is not null);
        if (assetId is null || offerId is null || Drive(dataset, "kind") != Kind || Drive(dataset, "schemaVersion") != SchemaVersion)
            return null;
        var recipient = Drive(dataset, "recipientBpn");
        var sender = Drive(dataset, "senderBpn");
        var manifest = Drive(dataset, "manifest");
        if (recipient is null || sender is null || manifest is null)
            return null;
        IReadOnlyList<ManifestItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<ManifestItem>>(manifest, ManifestJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (items is null || items.Count == 0 || items.Any(i => string.IsNullOrEmpty(i.Id) || string.IsNullOrEmpty(i.Sha256) || i.Size < 0))
            return null;
        DateTimeOffset? expiresAt = DateTimeOffset.TryParse(Drive(dataset, "expiresAt"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        var title = JsonLd.GetString(dataset, "title") ?? JsonLd.GetString(dataset, "name") ?? assetId;
        var revision = int.TryParse(Drive(dataset, "revision"), NumberStyles.None, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : 1;
        return new DeliveryOffer(assetId, offerId, title, Drive(dataset, "message"), recipient, sender, Drive(dataset, "senderOrganization"),
            expiresAt, items, Drive(dataset, "lineageId"), revision);
    }

    /// <summary>
    /// Eigene Properties nur über den vollen Namensraum oder das Präfix „drive:“ lesen, damit gleichnamige Properties
    /// anderer Vokabulare (etwa „kind“) nicht verwechselt werden.
    /// </summary>
    private static string? Drive(JsonObject dataset, string localName)
    {
        foreach (var (key, value) in dataset)
        {
            if (key == Namespace + localName || key == "drive:" + localName)
                return JsonLd.AsString(JsonLd.Unwrap(value));
        }
        return null;
    }
}
