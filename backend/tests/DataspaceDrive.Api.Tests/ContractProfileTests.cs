using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DataspaceDrive.Core.Access;
using static DataspaceDrive.Api.Tests.ApiTestHelpers;

namespace DataspaceDrive.Api.Tests;

/// <summary>
/// Vertragsprofile: der Anbieter legt die Contract-Policy des eingestellten Profils an und nennt das Profil im Asset,
/// der Empfänger verhandelt genau damit. Die Testumgebung lehnt abweichende Policies ab wie der Empfänger-EDC.
/// </summary>
public sealed class ContractProfileTests : IAsyncLifetime
{
    private readonly FakeDataspace _dataspace = new();
    private SharingApiFactory? _alice;
    private SharingApiFactory? _bob;
    private FakeDataspace.Participant _aliceEdc = null!;
    private FakeDataspace.Participant _bobEdc = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_alice is not null)
            await _alice.DisposeAsync();
        if (_bob is not null)
            await _bob.DisposeAsync();
    }

    private async Task SetUpAsync(string? configuredProfile)
    {
        _aliceEdc = _dataspace.Add("alice", "BPNL00000000WA01");
        _bobEdc = _dataspace.Add("bob", "BPNL00000000WB02");
        _alice = new SharingApiFactory(_dataspace, _aliceEdc,
            configuredProfile is null ? null : new Dictionary<string, string?> { ["Drive:Sharing:ContractProfile"] = configuredProfile });
        _bob = new SharingApiFactory(_dataspace, _bobEdc);
        _aliceEdc.ContentHandler = _alice.Server.CreateHandler();
        _bobEdc.ContentHandler = _bob.Server.CreateHandler();
        var aliceAdmin = await _alice.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);
        await ReadAsync(await aliceAdmin.PostAsJsonAsync("/api/admin/partners", new { bpn = _bobEdc.Bpn, name = "Bob", dspAddress = _bobEdc.DspAddress }));
        var bobAdmin = await _bob.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);
        await ReadAsync(await bobAdmin.PostAsJsonAsync("/api/admin/partners", new { bpn = _aliceEdc.Bpn, name = "Alice", dspAddress = _aliceEdc.DspAddress }));
    }

    [Theory]
    [InlineData(null, "open", "drive-cp-open-v1", null)]
    [InlineData("membership", "membership", "drive-cp-membership-v1", "Membership")]
    [InlineData("cx", "cx", "drive-cp-cx-v1", "windx.drive.fileExchange:1")]
    public async Task Profile_is_offered_and_negotiated_in_the_providers_form(string? configured, string profile, string policyId, string? marker)
    {
        await SetUpAsync(configured);
        var alice = await _alice!.ClientForAsync("alice-user", DrivePrincipal.RoleUser, DrivePrincipal.RoleExternalShare);
        var file = await UploadAsync(alice, await RootFolderAsync(alice), "Nutzung.pdf", "profil"u8.ToArray());
        var partnerId = (await ReadAsync(await alice.GetAsync("/api/partners/targets"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await alice.PostAsJsonAsync("/api/shares", new { fileIds = new[] { file.GetProperty("id").GetGuid() }, partnerIds = new[] { partnerId } }));
        await _alice.RunDeliveryWorkerAsync();

        var definition = _aliceEdc.ContractDefinitionBodies.Values.Single(cd => cd["@id"]!.GetValue<string>().StartsWith("drive-cd-", StringComparison.Ordinal)
            && cd["@id"]!.GetValue<string>() != "drive-cd-inbox-v1");
        Assert.Equal(policyId, definition["contractPolicyId"]!.GetValue<string>());
        Assert.StartsWith("drive-ap-", definition["accessPolicyId"]!.GetValue<string>());
        var asset = _aliceEdc.Assets.Values.Single(a => a["@id"]!.GetValue<string>().StartsWith("drive-d-", StringComparison.Ordinal));
        Assert.Equal(profile, asset["properties"]!["drive:contractProfile"]!.GetValue<string>());

        await _bob!.PollAsync();
        var bob = await _bob.ClientForAsync("bob-user", DrivePrincipal.RoleUser, DrivePrincipal.RoleInbox);
        var offerId = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = await RootFolderAsync(bob) }));
        await _bob.RunInboxAsync();

        Assert.Equal("Completed", (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("state").GetString());
        var requested = _bobEdc.ContractRequests.Single()["permission"];
        Assert.True(JsonNode.DeepEquals(_aliceEdc.Policies[policyId]["policy"]!["permission"], requested));
        if (marker is null)
            Assert.Empty(requested!.AsArray());
        else
            Assert.Contains(marker, requested!.ToJsonString());
    }
}
