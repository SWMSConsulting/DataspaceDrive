using System.Text.Json.Nodes;
using Swms.Edc.Client.Policies;

namespace Swms.Edc.Client.Requests;

public sealed record Criterion(string OperandLeft, string Operator, string OperandRight);

/// <summary>Request-Bodies der Management-API, wie sie im windx-Spike gegen tractusx-edc 0.12.1 funktioniert haben.</summary>
public static class EdcRequests
{
    private static JsonObject EdcContext() => new() { ["@vocab"] = EdcNamespaces.Edc };

    public static JsonObject QuerySpec(int offset, int limit, params Criterion[] filters)
    {
        var spec = new JsonObject
        {
            ["@context"] = EdcContext(),
            ["@type"] = "QuerySpec",
            ["offset"] = offset,
            ["limit"] = limit,
        };
        if (filters.Length > 0)
            spec["filterExpression"] = Filters(filters);
        return spec;
    }

    /// <summary>Contract Definition für genau ein Asset. Ein leerer Selektor würde alle Assets freigeben.</summary>
    public static JsonObject ContractDefinition(string id, string accessPolicyId, string contractPolicyId, string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        return new JsonObject
        {
            ["@context"] = EdcContext(),
            ["@type"] = "ContractDefinition",
            ["@id"] = id,
            ["accessPolicyId"] = accessPolicyId,
            ["contractPolicyId"] = contractPolicyId,
            ["assetsSelector"] = new JsonArray(new JsonObject
            {
                ["@type"] = "Criterion",
                ["operandLeft"] = EdcNamespaces.Edc + "id",
                ["operator"] = "=",
                ["operandRight"] = assetId,
            }),
        };
    }

    /// <summary>Discovery der DSP-Parameter eines Partners (Protokollversion, Adresse, Kennung).</summary>
    public static JsonObject DspVersionParams(string counterPartyId, string counterPartyAddress) => new()
    {
        ["@context"] = new JsonObject { ["tx"] = EdcNamespaces.Tx, ["edc"] = EdcNamespaces.Edc },
        ["@type"] = "tx:ConnectorParamsDiscoveryRequest",
        ["edc:counterPartyId"] = counterPartyId,
        ["edc:counterPartyAddress"] = counterPartyAddress,
    };

    public static JsonObject CatalogRequest(string counterPartyAddress, string counterPartyId, string protocol, int offset, int limit,
        params Criterion[] filters)
    {
        var querySpec = new JsonObject { ["@type"] = "QuerySpec", ["offset"] = offset, ["limit"] = limit };
        if (filters.Length > 0)
            querySpec["filterExpression"] = Filters(filters);
        return new JsonObject
        {
            ["@context"] = EdcContext(),
            ["@type"] = "CatalogRequest",
            ["counterPartyAddress"] = counterPartyAddress,
            ["counterPartyId"] = counterPartyId,
            ["protocol"] = protocol,
            ["querySpec"] = querySpec,
        };
    }

    /// <summary>
    /// Verhandlung über <c>POST /v3/edrs</c>: Aus dem Katalog kommt nur die Offer-ID, die Berechtigungen stammen
    /// aus dem Austauschprofil (<see cref="ExchangeProfile"/>), Assigner ist die Kennung des Anbieters aus der
    /// Discovery (unter DSP 2025-1 seine DID).
    /// </summary>
    public static JsonObject ContractRequest(string offerId, string providerId, string assetId, string counterPartyAddress,
        string protocol, JsonArray permissions) => new()
    {
        ["@context"] = ExchangeProfile.ProfileContext(),
        ["@type"] = "ContractRequest",
        ["counterPartyAddress"] = counterPartyAddress,
        ["protocol"] = protocol,
        ["policy"] = new JsonObject
        {
            ["@id"] = offerId,
            ["@type"] = "odrl:Offer",
            ["odrl:assigner"] = new JsonObject { ["@id"] = providerId },
            ["odrl:target"] = new JsonObject { ["@id"] = assetId },
            ["permission"] = permissions,
        },
    };

    /// <summary>Zieht ein Agreement vorzeitig zurück; laufende Transfers werden beendet, neue abgelehnt.</summary>
    public static JsonObject AgreementRetirement(string agreementId, string reason) => new()
    {
        ["@context"] = new JsonObject { ["@vocab"] = EdcNamespaces.Edc, ["tx"] = EdcNamespaces.Tx },
        ["@type"] = "AgreementsRetirementEntry",
        ["agreementId"] = agreementId,
        ["tx:reason"] = reason,
    };

    public static JsonObject TerminateTransfer(string reason) => new()
    {
        ["@context"] = EdcContext(),
        ["@type"] = "TerminateTransfer",
        ["reason"] = reason,
    };

    private static JsonArray Filters(IEnumerable<Criterion> filters) =>
        new(filters.Select(f => (JsonNode)new JsonObject
        {
            ["@type"] = "Criterion",
            ["operandLeft"] = f.OperandLeft,
            ["operator"] = f.Operator,
            ["operandRight"] = f.OperandRight,
        }).ToArray());
}
