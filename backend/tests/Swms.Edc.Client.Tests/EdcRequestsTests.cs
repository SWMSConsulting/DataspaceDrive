using System.Text.Json.Nodes;
using Swms.Edc.Client.Policies;
using Swms.Edc.Client.Requests;

namespace Swms.Edc.Client.Tests;

public class EdcRequestsTests
{
    private static void AssertJson(string expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());

    [Fact]
    public void Access_policy_combines_membership_and_bpn_with_full_iris()
    {
        var policy = ExchangeProfile.AccessPolicy("drive-ap-BPNL00000000WA01", ["BPNL00000000WA01"]);

        AssertJson("""
            {
              "@context": ["https://w3id.org/dspace/2025/1/odrl-profile.jsonld",
                           {"@vocab": "https://w3id.org/edc/v0.0.1/ns/", "odrl": "http://www.w3.org/ns/odrl/2/"}],
              "@type": "PolicyDefinition",
              "@id": "drive-ap-BPNL00000000WA01",
              "policy": {
                "@type": "Set",
                "permission": [{
                  "action": "https://w3id.org/catenax/2025/9/policy/access",
                  "constraint": {
                    "@type": "LogicalConstraint",
                    "and": [
                      {"leftOperand": "https://w3id.org/catenax/2025/9/policy/Membership", "operator": "eq", "rightOperand": "active"},
                      {"leftOperand": "https://w3id.org/catenax/2025/9/policy/BusinessPartnerNumber", "operator": "isAnyOf",
                       "rightOperand": ["BPNL00000000WA01"]}
                    ]
                  }
                }]
              }
            }
            """, policy);
    }

    [Fact]
    public void Contract_request_carries_only_the_offer_id_from_the_catalog()
    {
        var request = EdcRequests.ContractRequest("b2ZmZXI=:YXNzZXQ=:dXVpZA==", "did:web:bob-windx.cluster.swms-cloud.com",
            "drive-d-1", "https://bob-edc-windx.cluster.swms-cloud.com/api/v1/dsp/2025-1", EdcNamespaces.Dsp2025Protocol,
            ExchangeProfile.MembershipContractPermissions());

        // Form wie im Spike (Lauf 10111610) gegen tractusx-edc 0.12.1 erfolgreich verhandelt.
        AssertJson("""
            {
              "@context": ["https://w3id.org/dspace/2025/1/odrl-profile.jsonld",
                           {"@vocab": "https://w3id.org/edc/v0.0.1/ns/", "odrl": "http://www.w3.org/ns/odrl/2/"}],
              "@type": "ContractRequest",
              "counterPartyAddress": "https://bob-edc-windx.cluster.swms-cloud.com/api/v1/dsp/2025-1",
              "protocol": "dataspace-protocol-http:2025-1",
              "policy": {
                "@id": "b2ZmZXI=:YXNzZXQ=:dXVpZA==",
                "@type": "odrl:Offer",
                "odrl:assigner": {"@id": "did:web:bob-windx.cluster.swms-cloud.com"},
                "odrl:target": {"@id": "drive-d-1"},
                "permission": [{
                  "action": "https://w3id.org/catenax/2025/9/policy/access",
                  "constraint": {"leftOperand": "https://w3id.org/catenax/2025/9/policy/Membership", "operator": "eq",
                                 "rightOperand": "active"}
                }]
              }
            }
            """, request);
    }

    [Fact]
    public void Contract_definition_never_selects_all_assets() =>
        Assert.Throws<ArgumentException>(() => EdcRequests.ContractDefinition("cd", "ap", "cp", " "));

    [Fact]
    public void Retirement_uses_tx_namespace_for_the_reason()
    {
        var entry = EdcRequests.AgreementRetirement("agr-1", "Freigabe widerrufen");

        Assert.Equal("AgreementsRetirementEntry", entry["@type"]!.GetValue<string>());
        Assert.Equal("Freigabe widerrufen", entry["tx:reason"]!.GetValue<string>());
        Assert.Equal(EdcNamespaces.Tx, entry["@context"]!["tx"]!.GetValue<string>());
    }

    [Fact]
    public void Catalog_request_filters_on_own_properties()
    {
        var request = EdcRequests.CatalogRequest("https://alice/api/v1/dsp/2025-1", "did:web:alice", EdcNamespaces.Dsp2025Protocol, 0, 100,
            new Criterion("https://ns.swms-cloud.com/drive/v1/kind", "=", "delivery"));

        var filter = request["querySpec"]!["filterExpression"]![0]!;
        Assert.Equal("https://ns.swms-cloud.com/drive/v1/kind", filter["operandLeft"]!.GetValue<string>());
        Assert.Equal(100, request["querySpec"]!["limit"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("tractusx-0.12", "v4alpha/connectordiscovery/dspversionparams")]
    [InlineData("tractusx-0.13", "v3/connectordiscovery/dspversionparams")]
    public void Version_profiles_select_the_discovery_path(string name, string path) =>
        Assert.Equal(path, EdcVersionProfile.FromName(name).DspVersionDiscoveryPath);
}
