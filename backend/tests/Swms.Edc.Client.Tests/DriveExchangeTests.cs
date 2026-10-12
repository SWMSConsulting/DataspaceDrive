using System.Text.Json.Nodes;
using Swms.Edc.Client.Policies;

namespace Swms.Edc.Client.Tests;

public class DriveExchangeTests
{
    private static readonly DateTimeOffset Expires = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static DeliveryAssetSpec Spec(params ManifestItem[] items) => new(
        "0f3c", "drive-alice", "Wartungspflichtenheft Ü1", "Bitte bis Freitag prüfen", "BPNL00000000WB02", "BPNL00000000WA01",
        "Alice GmbH", Expires, items.Length > 0 ? items : [new ManifestItem("1", "Prüfbericht.pdf", 1234, new string('a', 64), "application/pdf")],
        "http://dataspace-drive-alice-edc.windx-alice.svc.cluster.local:8090", "v1.token");

    [Fact]
    public void Delivery_asset_carries_profile_properties_and_a_token_protected_data_address()
    {
        var asset = DriveExchange.DeliveryAsset(Spec());

        Assert.Equal("drive-d-0f3c", asset["@id"]!.GetValue<string>());
        var properties = asset["properties"]!.AsObject();
        Assert.Equal("delivery", properties["drive:kind"]!.GetValue<string>());
        Assert.Equal("1", properties["drive:schemaVersion"]!.GetValue<string>());
        Assert.Equal("BPNL00000000WB02", properties["drive:recipientBpn"]!.GetValue<string>());
        Assert.Equal("2026-11-10T12:00:00.0000000Z", properties["drive:expiresAt"]!.GetValue<string>());
        Assert.Equal("1234", properties["drive:totalSize"]!.GetValue<string>());
        // Manifest ASCII-escaped: Umlaute kommen als \u-Sequenz an.
        Assert.Contains("Pr\\u00FCfbericht.pdf", properties["drive:manifest"]!.GetValue<string>());
        Assert.Equal("0f3c", asset["privateProperties"]!["drive:deliveryId"]!.GetValue<string>());

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "@type": "DataAddress", "type": "HttpData",
              "baseUrl": "http://dataspace-drive-alice-edc.windx-alice.svc.cluster.local:8090/edc/deliveries/0f3c",
              "proxyPath": "true", "proxyQueryParams": "true", "proxyMethod": "false", "proxyBody": "false",
              "authKey": "X-Drive-Delivery-Token", "authCode": "v1.token"
            }
            """), asset["dataAddress"]), asset["dataAddress"]!.ToJsonString());
    }

    [Fact]
    public void Contract_definition_selects_exactly_the_delivery_asset()
    {
        var definition = DriveExchange.DeliveryContractDefinition("0f3c", "BPNL00000000WB02");

        Assert.Equal("drive-cd-0f3c", definition["@id"]!.GetValue<string>());
        Assert.Equal("drive-ap-BPNL00000000WB02", definition["accessPolicyId"]!.GetValue<string>());
        Assert.Equal(DriveExchange.MembershipContractPolicyId, definition["contractPolicyId"]!.GetValue<string>());
        var selector = Assert.Single(definition["assetsSelector"]!.AsArray())!;
        Assert.Equal("drive-d-0f3c", selector["operandRight"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(DriveExchange.OpenProfile, "drive-cp-open-v1", 0)]
    [InlineData(DriveExchange.MembershipProfile, "drive-cp-membership-v1", 1)]
    [InlineData(DriveExchange.CxProfile, "drive-cp-cx-v1", 1)]
    public void Each_profile_has_its_own_contract_policy_and_the_receiver_repeats_it(string profile, string policyId, int permissions)
    {
        Assert.True(DriveExchange.IsKnownProfile(profile));
        var policy = DriveExchange.ContractPolicy(profile);

        Assert.Equal(policyId, policy["@id"]!.GetValue<string>());
        Assert.Equal(permissions, policy["policy"]!["permission"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(policy["policy"]!["permission"], DriveExchange.PermissionsFor(profile)));
        Assert.Equal(policyId, DriveExchange.DeliveryContractDefinition("0f3c", "BPNL00000000WB02", profile)["contractPolicyId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, DriveExchange.MembershipProfile)]
    [InlineData("open", DriveExchange.OpenProfile)]
    [InlineData("cx", DriveExchange.CxProfile)]
    public void Stated_contract_profile_is_read_from_the_dataset(string? stated, string expected)
    {
        var asset = DriveExchange.DeliveryAsset(Spec());
        var dataset = new JsonObject { ["@id"] = asset["@id"]!.GetValue<string>(), ["odrl:hasPolicy"] = new JsonObject { ["@id"] = "offer" } };
        foreach (var (key, value) in asset["properties"]!.AsObject())
            if (key.StartsWith("drive:", StringComparison.Ordinal) && key != "drive:contractProfile")
                dataset[DriveExchange.Namespace + key["drive:".Length..]] = value!.DeepClone();
        if (stated is not null)
            dataset[DriveExchange.Namespace + "contractProfile"] = stated;

        Assert.Equal(expected, DriveExchange.ParseDataset(dataset)!.ContractProfile);
    }

    [Fact]
    public void Catalog_dataset_parses_back_into_the_offer()
    {
        var asset = DriveExchange.DeliveryAsset(Spec(
            new ManifestItem("1", "Prüfbericht.pdf", 1234, new string('a', 64), "application/pdf"),
            new ManifestItem("2", "Foto 1.jpg", 99, new string('b', 64), "image/jpeg")));
        // So liefert der Empfänger-EDC das Dataset: eigene Properties als volle IRI, Offer unter odrl:hasPolicy.
        var dataset = new JsonObject
        {
            ["@id"] = asset["@id"]!.GetValue<string>(),
            ["@type"] = "dcat:Dataset",
            ["odrl:hasPolicy"] = new JsonObject { ["@id"] = "b2ZmZXI=", ["@type"] = "odrl:Offer" },
            ["dct:title"] = "Wartungspflichtenheft Ü1",
            ["name"] = "Wartungspflichtenheft Ü1",
        };
        foreach (var (key, value) in asset["properties"]!.AsObject())
            if (key.StartsWith("drive:", StringComparison.Ordinal))
                dataset[DriveExchange.Namespace + key["drive:".Length..]] = value!.DeepClone();

        var offer = DriveExchange.ParseDataset(dataset)!;

        Assert.Equal("drive-d-0f3c", offer.AssetId);
        Assert.Equal("b2ZmZXI=", offer.OfferId);
        Assert.Equal("Wartungspflichtenheft Ü1", offer.Title);
        Assert.Equal("Bitte bis Freitag prüfen", offer.Message);
        Assert.Equal(("BPNL00000000WB02", "BPNL00000000WA01", "Alice GmbH"), (offer.RecipientBpn, offer.SenderBpn, offer.SenderOrganization));
        Assert.Equal(Expires, offer.ExpiresAt);
        Assert.Equal(["Prüfbericht.pdf", "Foto 1.jpg"], offer.Items.Select(i => i.Name));
        Assert.Equal(1333, offer.TotalSize);
    }

    [Theory]
    [InlineData("kind", "something-else")]
    [InlineData("schemaVersion", "2")]
    [InlineData("manifest", "kein json")]
    [InlineData("manifest", "[]")]
    [InlineData("contractProfile", "unbekannt")]
    public void Foreign_or_broken_datasets_are_ignored(string property, string value)
    {
        var dataset = new JsonObject
        {
            ["@id"] = "drive-d-1",
            ["odrl:hasPolicy"] = new JsonObject { ["@id"] = "offer" },
            [DriveExchange.Namespace + "kind"] = "delivery",
            [DriveExchange.Namespace + "schemaVersion"] = "1",
            [DriveExchange.Namespace + "recipientBpn"] = "BPNL00000000WB02",
            [DriveExchange.Namespace + "senderBpn"] = "BPNL00000000WA01",
            [DriveExchange.Namespace + "manifest"] = """[{"id":"1","name":"a.txt","size":1,"sha256":"aa","contentType":"text/plain"}]""",
        };
        Assert.NotNull(DriveExchange.ParseDataset(dataset));

        dataset[DriveExchange.Namespace + property] = value;

        Assert.Null(DriveExchange.ParseDataset(dataset));
    }
}
