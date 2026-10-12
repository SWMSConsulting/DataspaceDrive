using System.Net.Http.Json;
using DataspaceDrive.Core.Access;
using static DataspaceDrive.Api.Tests.ApiTestHelpers;

namespace DataspaceDrive.Api.Tests;

/// <summary>Vertragsprofil „cx“: Anbieter legt FrameworkAgreement + UsagePurpose an, der Empfänger verhandelt genau damit.</summary>
public sealed class ContractProfileTests : IAsyncLifetime
{
    private readonly FakeDataspace _dataspace = new();
    private SharingApiFactory _alice = null!;
    private SharingApiFactory _bob = null!;
    private FakeDataspace.Participant _aliceEdc = null!;
    private FakeDataspace.Participant _bobEdc = null!;

    public async Task InitializeAsync()
    {
        _aliceEdc = _dataspace.Add("alice", "BPNL00000000WA01");
        _bobEdc = _dataspace.Add("bob", "BPNL00000000WB02");
        _alice = new SharingApiFactory(_dataspace, _aliceEdc, new Dictionary<string, string?> { ["Drive:Sharing:ContractProfile"] = "cx" });
        _bob = new SharingApiFactory(_dataspace, _bobEdc);
        _aliceEdc.ContentHandler = _alice.Server.CreateHandler();
        _bobEdc.ContentHandler = _bob.Server.CreateHandler();
        var aliceAdmin = await _alice.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);
        await ReadAsync(await aliceAdmin.PostAsJsonAsync("/api/admin/partners", new { bpn = _bobEdc.Bpn, name = "Bob", dspAddress = _bobEdc.DspAddress }));
        var bobAdmin = await _bob.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);
        await ReadAsync(await bobAdmin.PostAsJsonAsync("/api/admin/partners", new { bpn = _aliceEdc.Bpn, name = "Alice", dspAddress = _aliceEdc.DspAddress }));
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
    }

    [Fact]
    public async Task Cx_profile_is_offered_and_negotiated_with_usage_permissions()
    {
        var alice = await _alice.ClientForAsync("alice-user", DrivePrincipal.RoleUser, DrivePrincipal.RoleExternalShare);
        var file = await UploadAsync(alice, await RootFolderAsync(alice), "Nutzung.pdf", "cx"u8.ToArray());
        var partnerId = (await ReadAsync(await alice.GetAsync("/api/partners/targets"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await alice.PostAsJsonAsync("/api/shares", new { fileIds = new[] { file.GetProperty("id").GetGuid() }, partnerIds = new[] { partnerId } }));
        await _alice.RunDeliveryWorkerAsync();

        Assert.Contains("drive-cp-cx-v1", _aliceEdc.Policies.Keys);
        Assert.Contains(_aliceEdc.ContractDefinitions.Values, cd => cd.AccessPolicyId.StartsWith("drive-ap-", StringComparison.Ordinal));
        var asset = _aliceEdc.Assets.Values.Single(a => a["@id"]!.GetValue<string>().StartsWith("drive-d-", StringComparison.Ordinal));
        Assert.Equal("cx", asset["properties"]!["drive:contractProfile"]!.GetValue<string>());

        await _bob.PollAsync();
        var bob = await _bob.ClientForAsync("bob-user", DrivePrincipal.RoleUser, DrivePrincipal.RoleInbox);
        var offerId = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = await RootFolderAsync(bob) }));
        await _bob.RunInboxAsync();

        Assert.Equal("Completed", (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("state").GetString());
        var policy = _bobEdc.ContractRequests.Single().ToJsonString();
        Assert.Contains("FrameworkAgreement", policy);
        Assert.Contains("windx.drive.fileExchange:1", policy);
    }
}
