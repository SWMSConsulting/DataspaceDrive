using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Swms.Edc.Client;
using Swms.Edc.Client.Policies;

namespace DataspaceDrive.Api.Tests;

/// <summary>
/// Dataspace im Testprozess: Management-APIs der Connectoren, Katalog mit Access-Policy-Prüfung, Verhandlung und EDR,
/// Agreement-Retirement sowie eine Dataplane, die Abrufe wie der EDC mit Zustell-Token, <c>Edc-Bpn</c> und
/// <c>Edc-Contract-Agreement-Id</c> an den Content-Endpunkt der Anbieter-Drive weiterreicht.
/// </summary>
public sealed class FakeDataspace
{
    private readonly List<Participant> _participants = [];
    private readonly Dictionary<string, Transfer> _transfers = [];
    private readonly object _lock = new();
    private int _sequence;

    public sealed class Participant(string name, string bpn)
    {
        public string Name { get; } = name;
        public string Bpn { get; } = bpn;
        public string Did => $"did:web:{Name}";
        public string DspAddress => $"https://{Name}-edc/api/v1/dsp";
        public string ManagementHost => $"edc-{Name}";
        public string DataplaneHost => $"dataplane-{Name}";
        public string ContentHost => $"drive-{Name}-content";

        /// <summary>Handler des Content-Ports der Drive dieses Teilnehmers (TestServer).</summary>
        public HttpMessageHandler? ContentHandler { get; set; }

        public Dictionary<string, JsonObject> Policies { get; } = [];
        public Dictionary<string, JsonObject> Assets { get; } = [];
        public Dictionary<string, (string AssetId, string AccessPolicyId)> ContractDefinitions { get; } = [];

        /// <summary>Contract Definitions so, wie sie angelegt wurden (für die Abfrage der Reconciliation).</summary>
        public Dictionary<string, JsonObject> ContractDefinitionBodies { get; } = [];
        public Dictionary<string, (string AssetId, Participant Consumer)> Agreements { get; } = [];
        public HashSet<string> Retired { get; } = [];
        public Dictionary<string, Negotiation> Negotiations { get; } = [];
        public List<string> TerminatedTransfers { get; } = [];

        /// <summary>Policies, die dieser Teilnehmer als Konsument verhandelt hat (für Profil-Tests).</summary>
        public List<JsonObject> ContractRequests { get; } = [];
    }

    public sealed record Negotiation(string Id, string State, string? AgreementId, string? TransferProcessId, string? ErrorDetail);

    private sealed record Transfer(string Id, Participant Provider, Participant Consumer, string AgreementId);

    public Participant Add(string name, string bpn)
    {
        var participant = new Participant(name, bpn);
        _participants.Add(participant);
        return participant;
    }

    public HttpMessageHandler CreateHandler() => new Router(this);

    private sealed class Router(FakeDataspace dataspace) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => dataspace.RouteAsync(request, ct);
    }

    private async Task<HttpResponseMessage> RouteAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri!.Host;
        if (_participants.FirstOrDefault(p => p.ManagementHost == host) is { } management)
        {
            var body = request.Content is null ? null : await request.Content.ReadFromJsonAsync<JsonObject>(ct);
            lock (_lock)
                return Management(management, request.Method, request.RequestUri.AbsolutePath["/management/".Length..], body);
        }
        if (_participants.FirstOrDefault(p => p.DataplaneHost == host) is { } provider)
            return await DataplaneAsync(provider, request, ct);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Management(Participant self, HttpMethod method, string path, JsonObject? body)
    {
        var segments = path.Split('/');
        switch (method.Method, path)
        {
            case ("POST", "v4alpha/connectordiscovery/dspversionparams"):
            {
                var bpn = JsonLd.GetString(body, "counterPartyId");
                var target = _participants.FirstOrDefault(p => p.Bpn == bpn);
                return target is null
                    ? Json(HttpStatusCode.NotFound, """[{"message":"unknown"}]""")
                    : Json(HttpStatusCode.OK, new JsonObject
                    {
                        ["protocol"] = EdcNamespaces.Dsp2025Protocol,
                        ["counterPartyAddress"] = JsonLd.GetString(body, "counterPartyAddress") + "/2025-1",
                        ["counterPartyId"] = target.Did,
                    });
            }
            case ("POST", "v3/policydefinitions"):
                return Create(self.Policies, body!);
            case ("POST", "v3/assets"):
                return Create(self.Assets, body!);
            case ("POST", "v3/contractdefinitions"):
            {
                var id = JsonLd.Id(body)!;
                if (self.ContractDefinitions.ContainsKey(id))
                    return Json(HttpStatusCode.Conflict, "[]");
                var assetId = body!["assetsSelector"]![0]!["operandRight"]!.GetValue<string>();
                self.ContractDefinitions[id] = (assetId, body["accessPolicyId"]!.GetValue<string>());
                self.ContractDefinitionBodies[id] = body;
                return Json(HttpStatusCode.OK, new JsonObject { ["@id"] = id });
            }
            case ("POST", "v3/contractdefinitions/request"):
            {
                var offset = body?["offset"]?.GetValue<int>() ?? 0;
                return Json(HttpStatusCode.OK, new JsonArray(offset > 0 ? [] : self.ContractDefinitionBodies.Values.Select(b => (JsonNode)b.DeepClone()).ToArray()));
            }
            case ("POST", "v3/contractagreements/request"):
            {
                var assetId = Filter(body, "assetId");
                return Json(HttpStatusCode.OK, new JsonArray(self.Agreements.Where(a => a.Value.AssetId == assetId)
                    .Select(a => (JsonNode)AgreementJson(self, a.Key)).ToArray()));
            }
            case ("POST", "v3/contractagreements/retirements"):
            {
                var agreementId = JsonLd.GetString(body, "agreementId")!;
                return self.Retired.Add(agreementId) ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.Conflict, "[]");
            }
            case ("POST", "v3/contractagreements/retirements/request"):
            {
                var agreementId = Filter(body, "agreementId");
                return Json(HttpStatusCode.OK, new JsonArray(self.Retired.Where(r => r == agreementId)
                    .Select(r => (JsonNode)new JsonObject { ["agreementId"] = r }).ToArray()));
            }
            case ("POST", "v3/catalog/request"):
                return Catalog(self, body!);
            case ("POST", "v3/edrs"):
                return StartNegotiation(self, body!);
            case ("POST", "v3/edrs/request"):
            {
                var negotiationId = Filter(body, "contractNegotiationId");
                var negotiation = self.Negotiations.GetValueOrDefault(negotiationId ?? "");
                return Json(HttpStatusCode.OK, negotiation?.TransferProcessId is { } tp
                    ? new JsonArray(new JsonObject { ["@id"] = tp, ["transferProcessId"] = tp, ["contractNegotiationId"] = negotiation.Id })
                    : new JsonArray());
            }
        }
        if (method == HttpMethod.Put && segments is ["v3", "policydefinitions", var policyToUpdate])
        {
            if (!self.Policies.ContainsKey(policyToUpdate))
                return Json(HttpStatusCode.NotFound, "[]");
            self.Policies[policyToUpdate] = body!;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (method == HttpMethod.Put && path == "v3/assets")
        {
            var assetToUpdate = JsonLd.Id(body)!;
            if (!self.Assets.ContainsKey(assetToUpdate))
                return Json(HttpStatusCode.NotFound, "[]");
            self.Assets[assetToUpdate] = body!;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (method == HttpMethod.Delete && segments is ["v3", "contractdefinitions", var cdId])
        {
            self.ContractDefinitionBodies.Remove(cdId);
            return self.ContractDefinitions.Remove(cdId) ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.NotFound, "[]");
        }
        if (method == HttpMethod.Delete && segments is ["v3", "assets", var assetToDelete])
        {
            if (self.Agreements.Values.Any(a => a.AssetId == assetToDelete))
                return Json(HttpStatusCode.Conflict, "[]");
            return self.Assets.Remove(assetToDelete) ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.NotFound, "[]");
        }
        if (method == HttpMethod.Get && segments is ["v3", "contractagreements", var agreementToRead])
            return self.Agreements.ContainsKey(agreementToRead) ? Json(HttpStatusCode.OK, AgreementJson(self, agreementToRead)) : Json(HttpStatusCode.NotFound, "[]");
        if (method == HttpMethod.Get && segments is ["v3", "contractnegotiations", var negotiationToRead]
            && self.Negotiations.TryGetValue(negotiationToRead, out var found))
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["@id"] = found.Id, ["state"] = found.State, ["contractAgreementId"] = found.AgreementId, ["errorDetail"] = found.ErrorDetail,
            });
        if (method == HttpMethod.Get && segments is ["v3", "edrs", var transferToRead, "dataaddress"] && _transfers.TryGetValue(transferToRead, out var transfer))
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["endpoint"] = $"http://{transfer.Provider.DataplaneHost}/api/public", ["authorization"] = $"edr-{transferToRead}",
            });
        if (method == HttpMethod.Post && segments is ["v3", "transferprocesses", var transferToEnd, "terminate"])
        {
            self.TerminatedTransfers.Add(transferToEnd);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return Json(HttpStatusCode.NotFound, $$"""[{"message":"Fake kennt {{method}} {{path}} nicht"}]""");
    }

    private HttpResponseMessage Catalog(Participant consumer, JsonObject request)
    {
        var address = JsonLd.GetString(request, "counterPartyAddress")!;
        var provider = _participants.Single(p => address.StartsWith(p.DspAddress, StringComparison.Ordinal));
        var filters = JsonLd.Objects(request["querySpec"]?["filterExpression"]).ToList();
        var datasets = new JsonArray();
        foreach (var (cdId, cd) in provider.ContractDefinitions)
        {
            if (!AccessAllowed(provider, cd.AccessPolicyId, consumer) || !provider.Assets.TryGetValue(cd.AssetId, out var asset))
                continue;
            var dataset = new JsonObject
            {
                ["@id"] = cd.AssetId, ["@type"] = "dcat:Dataset",
                ["odrl:hasPolicy"] = new JsonObject { ["@id"] = OfferId(cdId, cd.AssetId), ["@type"] = "odrl:Offer" },
            };
            foreach (var (key, value) in asset["properties"]!.AsObject())
                dataset[key.StartsWith("drive:", StringComparison.Ordinal) ? DriveExchange.Namespace + key["drive:".Length..] : key] = value!.DeepClone();
            if (filters.All(f => dataset[f["operandLeft"]!.GetValue<string>()]?.GetValue<string>() == f["operandRight"]!.GetValue<string>()))
                datasets.Add(dataset);
        }
        // Wie der EDC: ein einzelnes Dataset kommt als Objekt, mehrere als Liste.
        return Json(HttpStatusCode.OK, new JsonObject
        {
            ["@type"] = "dcat:Catalog",
            ["dcat:dataset"] = datasets.Count == 1 ? datasets[0]!.DeepClone() : datasets,
        });
    }

    private HttpResponseMessage StartNegotiation(Participant consumer, JsonObject request)
    {
        consumer.ContractRequests.Add(request["policy"]!.AsObject().DeepClone().AsObject());
        var offerId = request["policy"]!["@id"]!.GetValue<string>();
        var assigner = request["policy"]!["odrl:assigner"]!["@id"]!.GetValue<string>();
        var (cdId, assetId) = ParseOffer(offerId);
        var provider = _participants.Single(p => p.Did == assigner);
        var negotiationId = Next("neg");
        if (!provider.ContractDefinitions.TryGetValue(cdId, out var cd) || cd.AssetId != assetId || !AccessAllowed(provider, cd.AccessPolicyId, consumer))
        {
            consumer.Negotiations[negotiationId] = new Negotiation(negotiationId, "TERMINATED", null, null, "Contract offer not found or not accessible");
        }
        else if (!SamePermissions(provider, cdId, request["policy"]!["permission"]))
        {
            // Wie der Empfänger-EDC (PolicyEquality): das Agreement trägt die Policy des Anbieters, die Anfrage muss ihr gleichen.
            consumer.Negotiations[negotiationId] = new Negotiation(negotiationId, "TERMINATED", null, null,
                "Policy in the contract agreement is not equal to the one in the contract offer");
        }
        else
        {
            var agreementId = Next("agr");
            provider.Agreements[agreementId] = (assetId, consumer);
            var transferId = Next("tp");
            _transfers[transferId] = new Transfer(transferId, provider, consumer, agreementId);
            consumer.Negotiations[negotiationId] = new Negotiation(negotiationId, "FINALIZED", Next("consumer-agr"), transferId, null);
        }
        return Json(HttpStatusCode.OK, new JsonObject { ["@id"] = negotiationId });
    }

    private static bool SamePermissions(Participant provider, string cdId, JsonNode? requested)
    {
        var contractPolicyId = provider.ContractDefinitionBodies[cdId]["contractPolicyId"]!.GetValue<string>();
        var offered = provider.Policies.GetValueOrDefault(contractPolicyId)?["policy"]?["permission"];
        return JsonNode.DeepEquals(offered ?? new JsonArray(), requested ?? new JsonArray());
    }

    private async Task<HttpResponseMessage> DataplaneAsync(Participant provider, HttpRequestMessage request, CancellationToken ct)
    {
        Transfer? transfer;
        string? baseUrl;
        string? authKey;
        string? authCode;
        lock (_lock)
        {
            var token = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "";
            transfer = _transfers.GetValueOrDefault(token.StartsWith("edr-", StringComparison.Ordinal) ? token[4..] : "");
            if (transfer is null || transfer.Provider != provider || provider.Retired.Contains(transfer.AgreementId))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            var dataAddress = provider.Assets[provider.Agreements[transfer.AgreementId].AssetId]["dataAddress"]!;
            baseUrl = dataAddress["baseUrl"]!.GetValue<string>();
            authKey = dataAddress["authKey"]!.GetValue<string>();
            authCode = dataAddress["authCode"]!.GetValue<string>();
        }
        var suffix = request.RequestUri!.AbsolutePath["/api/public".Length..];
        using var forward = new HttpRequestMessage(HttpMethod.Get, baseUrl + suffix + request.RequestUri.Query);
        forward.Headers.Add(authKey, authCode);
        forward.Headers.Add("Edc-Bpn", transfer.Consumer.Bpn);
        forward.Headers.Add("Edc-Contract-Agreement-Id", transfer.AgreementId);
        forward.Headers.Add("X-Test-Local-Port", DriveApiFactory.ContentPort.ToString());
        using var client = new HttpClient(provider.ContentHandler!, disposeHandler: false);
        var response = await client.SendAsync(forward, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return new HttpResponseMessage(response.StatusCode) { Content = new ByteArrayContent(bytes) };
    }

    private static bool AccessAllowed(Participant provider, string accessPolicyId, Participant consumer) =>
        provider.Policies.TryGetValue(accessPolicyId, out var policy) && policy.ToJsonString().Contains($"\"{consumer.Bpn}\"", StringComparison.Ordinal);

    private static string OfferId(string cdId, string assetId) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cdId}:{assetId}:{Guid.NewGuid()}"));

    private static (string CdId, string AssetId) ParseOffer(string offerId)
    {
        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(offerId)).Split(':');
        return (parts[0], parts[1]);
    }

    private static JsonObject AgreementJson(Participant provider, string agreementId) => new()
    {
        ["@id"] = agreementId, ["edc:assetId"] = provider.Agreements[agreementId].AssetId,
        ["edc:consumerId"] = provider.Agreements[agreementId].Consumer.Did, ["edc:providerId"] = provider.Did,
    };

    private static string? Filter(JsonObject? querySpec, string operand) =>
        JsonLd.Objects(querySpec?["filterExpression"]).FirstOrDefault(f => f["operandLeft"]?.GetValue<string>() == operand)?["operandRight"]?.GetValue<string>();

    private static HttpResponseMessage Create(Dictionary<string, JsonObject> store, JsonObject body)
    {
        var id = JsonLd.Id(body)!;
        if (store.ContainsKey(id))
            return Json(HttpStatusCode.Conflict, "[]");
        store[id] = body;
        return Json(HttpStatusCode.OK, new JsonObject { ["@id"] = id });
    }

    private string Next(string prefix) => $"{prefix}-{Interlocked.Increment(ref _sequence)}";

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) => Json(status, body.ToJsonString());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
