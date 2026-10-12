using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Swms.Edc.Client.Requests;

namespace Swms.Edc.Client;

public sealed record DspParams(string Protocol, string CounterPartyAddress, string CounterPartyId);

public sealed record ContractAgreementInfo(string Id, string AssetId, string? ConsumerId, string? ProviderId);

public sealed record NegotiationInfo(string Id, string State, string? AgreementId, string? ErrorDetail)
{
    public bool IsFinalized => State == "FINALIZED";
    public bool IsTerminated => State is "TERMINATED" or "TERMINATING";
}

/// <summary>EDR für einen PULL-Abruf: Public-API-Endpunkt der Anbieter-Dataplane und Token (ohne „Bearer“).</summary>
public sealed record EdrDataAddress(string Endpoint, string Authorization);

public enum CreateOutcome
{
    Created,
    AlreadyExists,
}

public sealed class EdcException(HttpStatusCode status, string operation, string detail)
    : Exception($"EDC {operation}: {(int)status} {detail}")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Typisierter Zugriff auf die Management-API eines Tractus-X-EDC. Der <see cref="HttpClient"/> trägt die
/// Management-URL als BaseAddress (mit abschließendem „/“) und den Header <c>X-Api-Key</c>.
/// </summary>
public sealed class EdcManagementClient(HttpClient http, EdcVersionProfile profile)
{
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromSeconds(90);

    public EdcVersionProfile Profile { get; } = profile;

    /// <summary>Protokoll, Adresse und Kennung (unter DSP 2025-1 die DID) eines Partners; die BPN löst der EDC über das BDRS auf.</summary>
    public async Task<DspParams> DiscoverDspAsync(string counterPartyId, string counterPartyAddress, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, Profile.DspVersionDiscoveryPath, EdcRequests.DspVersionParams(counterPartyId, counterPartyAddress),
            "Discovery", ct);
        var obj = result as JsonObject ?? throw new EdcException(HttpStatusCode.BadGateway, "Discovery", "leere Antwort");
        return new DspParams(
            JsonLd.GetString(obj, "protocol") ?? throw new EdcException(HttpStatusCode.BadGateway, "Discovery", "ohne Protokoll"),
            JsonLd.GetString(obj, "counterPartyAddress") ?? counterPartyAddress,
            JsonLd.GetString(obj, "counterPartyId") ?? counterPartyId);
    }

    public Task<CreateOutcome> CreatePolicyAsync(JsonObject definition, CancellationToken ct) =>
        CreateAsync("v3/policydefinitions", definition, "Policy anlegen", ct);

    public Task<CreateOutcome> CreateAssetAsync(JsonObject asset, CancellationToken ct) =>
        CreateAsync("v3/assets", asset, "Asset anlegen", ct);

    public Task<CreateOutcome> CreateContractDefinitionAsync(JsonObject definition, CancellationToken ct) =>
        CreateAsync("v3/contractdefinitions", definition, "Contract Definition anlegen", ct);

    /// <summary>False, wenn es die Contract Definition nicht (mehr) gibt.</summary>
    public async Task<bool> DeleteContractDefinitionAsync(string id, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"v3/contractdefinitions/{Uri.EscapeDataString(id)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "Contract Definition löschen", ct);
        return true;
    }

    public async Task<ContractAgreementInfo?> GetAgreementAsync(string agreementId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"v3/contractagreements/{Uri.EscapeDataString(agreementId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "Agreement lesen", ct);
        return ToAgreement(await response.Content.ReadFromJsonAsync<JsonObject>(ct));
    }

    public async Task<IReadOnlyList<ContractAgreementInfo>> QueryAgreementsForAssetAsync(string assetId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, "v3/contractagreements/request",
            EdcRequests.QuerySpec(0, 1000, new Criterion("assetId", "=", assetId)), "Agreements suchen", ct);
        return JsonLd.Objects(result).Select(ToAgreement).OfType<ContractAgreementInfo>().ToList();
    }

    /// <summary>Zieht ein Agreement zurück; ein bereits zurückgezogenes gilt als Erfolg.</summary>
    public async Task RetireAgreementAsync(string agreementId, string reason, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("v3/contractagreements/retirements", EdcRequests.AgreementRetirement(agreementId, reason), ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return;
        await EnsureSuccessAsync(response, "Agreement zurückziehen", ct);
    }

    public async Task<bool> IsAgreementRetiredAsync(string agreementId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, "v3/contractagreements/retirements/request",
            EdcRequests.QuerySpec(0, 10, new Criterion("agreementId", "=", agreementId)), "Retirements suchen", ct);
        return JsonLd.Objects(result).Count > 0;
    }

    public async Task<JsonObject> RequestCatalogAsync(string counterPartyAddress, string counterPartyId, string protocol, int offset, int limit,
        IReadOnlyList<Criterion> filters, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CatalogTimeout);
        var result = await SendAsync(HttpMethod.Post, "v3/catalog/request",
            EdcRequests.CatalogRequest(counterPartyAddress, counterPartyId, protocol, offset, limit, [.. filters]), "Katalog abfragen", cts.Token);
        return result as JsonObject ?? new JsonObject();
    }

    /// <summary>Startet Verhandlung und Transfer in einem Schritt (<c>POST /v3/edrs</c>) und liefert die Negotiation-ID.</summary>
    public async Task<string> StartEdrNegotiationAsync(JsonObject contractRequest, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, "v3/edrs", contractRequest, "Verhandlung starten", ct);
        return JsonLd.Id(result as JsonObject) ?? throw new EdcException(HttpStatusCode.BadGateway, "Verhandlung starten", "ohne ID");
    }

    public async Task<NegotiationInfo> GetNegotiationAsync(string negotiationId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Get, $"v3/contractnegotiations/{Uri.EscapeDataString(negotiationId)}", null, "Verhandlung lesen", ct);
        var obj = result as JsonObject;
        return new NegotiationInfo(negotiationId, JsonLd.GetString(obj, "state") ?? "UNKNOWN", JsonLd.GetString(obj, "contractAgreementId"),
            JsonLd.GetString(obj, "errorDetail"));
    }

    /// <summary>Transferprozess des EDR zu einer Verhandlung, sobald der EDC ihn angelegt hat.</summary>
    public async Task<string?> FindEdrTransferProcessAsync(string negotiationId, CancellationToken ct)
    {
        var result = await SendAsync(HttpMethod.Post, "v3/edrs/request",
            EdcRequests.QuerySpec(0, 50, new Criterion("contractNegotiationId", "=", negotiationId)), "EDR suchen", ct);
        var entry = JsonLd.Objects(result).FirstOrDefault();
        return entry is null ? null : JsonLd.GetString(entry, "transferProcessId") ?? JsonLd.Id(entry);
    }

    /// <summary>Aktuelle Zugangsdaten zum Transfer; der EDC erneuert abgelaufene Tokens selbst (auto_refresh).</summary>
    public async Task<EdrDataAddress?> GetEdrDataAddressAsync(string transferProcessId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"v3/edrs/{Uri.EscapeDataString(transferProcessId)}/dataaddress?auto_refresh=true", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "EDR lesen", ct);
        var obj = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var endpoint = JsonLd.GetString(obj, "endpoint");
        var authorization = JsonLd.GetString(obj, "authorization");
        return endpoint is null || authorization is null ? null : new EdrDataAddress(endpoint, authorization);
    }

    public async Task TerminateTransferAsync(string transferProcessId, string reason, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"v3/transferprocesses/{Uri.EscapeDataString(transferProcessId)}/terminate",
            EdcRequests.TerminateTransfer(reason), ct);
        // Schon beendete Transfers meldet der EDC mit 409; das ist hier kein Fehler.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            return;
        await EnsureSuccessAsync(response, "Transfer beenden", ct);
    }

    private async Task<CreateOutcome> CreateAsync(string path, JsonObject body, string operation, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(path, body, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return CreateOutcome.AlreadyExists;
        await EnsureSuccessAsync(response, operation, ct);
        return CreateOutcome.Created;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, operation, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new EdcException(response.StatusCode, operation, detail.Length > 500 ? detail[..500] : detail);
    }

    private static ContractAgreementInfo? ToAgreement(JsonObject? obj)
    {
        var id = JsonLd.Id(obj);
        var assetId = JsonLd.GetString(obj, "assetId");
        return id is null || assetId is null
            ? null
            : new ContractAgreementInfo(id, assetId, JsonLd.GetString(obj, "consumerId"), JsonLd.GetString(obj, "providerId"));
    }
}
