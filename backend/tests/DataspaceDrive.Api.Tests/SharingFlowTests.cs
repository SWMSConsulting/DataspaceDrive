using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Infrastructure.Inbox;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using DataspaceDrive.Core.Shares;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DataspaceDrive.Api.Tests.ApiTestHelpers;

namespace DataspaceDrive.Api.Tests;

/// <summary>Zeit, die der Test vorstellt; die Hintergrunddienste warten sonst echte Sekunden.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Drive eines Teilnehmers im <see cref="FakeDataspace"/>; Hintergrunddienste ruft der Test selbst auf.</summary>
public sealed class SharingApiFactory(FakeDataspace dataspace, FakeDataspace.Participant participant) : DriveApiFactory
{
    public ManualTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Drive:Participant:Id"] = participant.Did,
            ["Drive:Participant:Bpn"] = participant.Bpn,
            ["Drive:Participant:DspAddress"] = participant.DspAddress,
            ["Drive:Participant:Organization"] = participant.Name,
            ["Drive:Edc:ManagementUrl"] = $"http://{participant.ManagementHost}/management",
            ["Drive:Edc:ApiKey"] = "test-key",
            ["Drive:Sharing:ContentBaseUrl"] = $"http://{participant.ContentHost}",
            ["Drive:Sharing:InstanceId"] = $"drive-{participant.Name}",
            ["Drive:Inbox:SegmentSize"] = (1024 * 1024).ToString(),
            ["Drive:BackgroundWorkers:Enabled"] = "false",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            // Die Basis ersetzt das Zustellverzeichnis durch eine Attrappe; hier gilt das echte.
            services.RemoveAll<IDeliveryDirectory>();
            services.AddScoped<IDeliveryDirectory, DeliveryDirectory>();
            services.AddHttpClient(EdcConnection.HttpClientName).ConfigurePrimaryHttpMessageHandler(dataspace.CreateHandler);
            services.AddHttpClient(IncomingTransferWorker.DataplaneClient).ConfigurePrimaryHttpMessageHandler(dataspace.CreateHandler);
        });
    }

    public async Task RunDeliveryWorkerAsync()
    {
        var worker = ActivatorUtilities.CreateInstance<DeliveryWorker>(Services);
        while (await worker.ProcessNextAsync(CancellationToken.None))
        {
        }
    }

    /// <summary>Lässt den Eingang laufen, bis nichts mehr ansteht; die Uhr springt über Wartezeiten.</summary>
    public async Task RunInboxAsync()
    {
        var worker = ActivatorUtilities.CreateInstance<IncomingTransferWorker>(Services);
        for (var i = 0; i < 50; i++)
        {
            if (!await worker.ProcessNextAsync(CancellationToken.None))
            {
                Clock.Advance(TimeSpan.FromSeconds(5));
                if (!await worker.ProcessNextAsync(CancellationToken.None))
                    break;
            }
        }
        await worker.CompleteScannedAsync(CancellationToken.None);
    }

    public Task<int> PollAsync() => Services.GetRequiredService<InboxPoller>().PollAllAsync(CancellationToken.None);
}

public sealed class SharingFlowTests : IAsyncLifetime
{
    private const string AliceBpn = "BPNL00000000WA01";
    private const string BobBpn = "BPNL00000000WB02";

    private readonly FakeDataspace _dataspace = new();
    private SharingApiFactory _alice = null!;
    private SharingApiFactory _bob = null!;
    private FakeDataspace.Participant _aliceEdc = null!;
    private FakeDataspace.Participant _bobEdc = null!;

    public async Task InitializeAsync()
    {
        _aliceEdc = _dataspace.Add("alice", AliceBpn);
        _bobEdc = _dataspace.Add("bob", BobBpn);
        _dataspace.Add("dave", "BPNL00000000WD04");
        _alice = new SharingApiFactory(_dataspace, _aliceEdc);
        _bob = new SharingApiFactory(_dataspace, _bobEdc);
        _aliceEdc.ContentHandler = _alice.Server.CreateHandler();
        _bobEdc.ContentHandler = _bob.Server.CreateHandler();
        await AddPartnerAsync(_alice, "Bob GmbH", BobBpn, _bobEdc.DspAddress);
        await AddPartnerAsync(_bob, "Alice AG", AliceBpn, _aliceEdc.DspAddress);
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
    }

    private static async Task<JsonElement> AddPartnerAsync(SharingApiFactory factory, string name, string bpn, string address)
    {
        var admin = await factory.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);
        return await ReadAsync(await admin.PostAsJsonAsync("/api/admin/partners", new { bpn, name, dspAddress = address }));
    }

    private Task<HttpClient> AliceSharerAsync() =>
        _alice.ClientForAsync("alice-user", DrivePrincipal.RoleUser, DrivePrincipal.RoleExternalShare);

    private Task<HttpClient> BobInboxAsync() => _bob.ClientForAsync("bob-user", DrivePrincipal.RoleUser, DrivePrincipal.RoleInbox);

    private async Task<(HttpClient Client, JsonElement Share, byte[] Content)> ShareFileAsync(string name, int size)
    {
        var client = await AliceSharerAsync();
        var content = RandomNumberGenerator.GetBytes(size);
        var file = await UploadAsync(client, await RootFolderAsync(client), name, content);
        var target = (await ReadAsync(await client.GetAsync("/api/partners/targets"))).EnumerateArray().Single();
        var share = await ReadAsync(await client.PostAsJsonAsync("/api/shares", new
        {
            fileIds = new[] { file.GetProperty("id").GetGuid() }, partnerIds = new[] { target.GetProperty("id").GetGuid() },
            title = "Wartungspflichtenheft", message = "Bitte prüfen",
        }));
        await _alice.RunDeliveryWorkerAsync();
        return (client, share, content);
    }

    [Fact]
    public async Task Partner_is_resolved_through_the_connector()
    {
        var admin = await _alice.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);

        var partner = (await ReadAsync(await admin.GetAsync("/api/admin/partners"))).EnumerateArray().Single();

        Assert.Equal("did:web:bob", partner.GetProperty("did").GetString());
        Assert.Equal("dataspace-protocol-http:2025-1", partner.GetProperty("protocol").GetString());
        Assert.Equal(JsonValueKind.Null, partner.GetProperty("resolveError").ValueKind);
    }

    [Fact]
    public async Task Shared_file_arrives_with_identical_checksum_and_the_retrieval_is_logged()
    {
        var (alice, share, content) = await ShareFileAsync("Prüfbericht.pdf", 3 * 1024 * 1024 + 17);
        var delivery = share.GetProperty("deliveries")[0];
        var deliveryId = delivery.GetProperty("id").GetGuid();
        Assert.Contains($"drive-d-{deliveryId:N}", _aliceEdc.Assets.Keys);
        Assert.Contains($"drive-cd-{deliveryId:N}", _aliceEdc.ContractDefinitions.Keys);

        Assert.Equal(1, await _bob.PollAsync());
        var bob = await BobInboxAsync();
        var offer = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single();
        Assert.Equal("New", offer.GetProperty("state").GetString());
        Assert.Equal("Wartungspflichtenheft", offer.GetProperty("title").GetString());
        Assert.Equal("Bitte prüfen", offer.GetProperty("message").GetString());
        Assert.Equal("Prüfbericht.pdf", offer.GetProperty("items")[0].GetProperty("name").GetString());

        var bobRoot = await RootFolderAsync(bob);
        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offer.GetProperty("id").GetGuid()}/accept", new { folderId = bobRoot }));
        await _bob.RunInboxAsync();

        var done = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single();
        Assert.Equal("Completed", done.GetProperty("state").GetString());
        var stored = (await ReadAsync(await bob.GetAsync($"/api/folders/{bobRoot}/children"))).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "Prüfbericht.pdf");
        var downloaded = await (await bob.GetAsync($"/api/files/{stored.GetProperty("id").GetGuid()}/content")).Content.ReadAsByteArrayAsync();
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(downloaded));
        Assert.Single(_bobEdc.TerminatedTransfers);

        // Anbieterseite: 4 Abschnitte à 1 MiB, ein Abruf gezählt, alle mit Bobs BPN.
        var retrievals = await ReadAsync(await alice.GetAsync($"/api/deliveries/{deliveryId}/retrievals"));
        Assert.Equal(4, retrievals.GetArrayLength());
        Assert.All(retrievals.EnumerateArray(), r => Assert.Equal(("Allowed", BobBpn),
            (r.GetProperty("decision").GetString(), r.GetProperty("consumerBpn").GetString())));
        var listed = (await ReadAsync(await alice.GetAsync("/api/shares"))).EnumerateArray().Single().GetProperty("deliveries")[0];
        Assert.Equal(1, listed.GetProperty("retrievalCount").GetInt32());
    }

    [Fact]
    public async Task Revocation_withdraws_the_offer_and_blocks_further_retrieval()
    {
        var (alice, share, _) = await ShareFileAsync("Angebot.pdf", 2048);
        var deliveryId = share.GetProperty("deliveries")[0].GetProperty("id").GetGuid();
        await _bob.PollAsync();
        // Bob verhandelt schon (Agreement existiert), dann widerruft Alice.
        var bob = await BobInboxAsync();
        var offerId = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = await RootFolderAsync(bob) }));
        var transfer = ActivatorUtilities.CreateInstance<IncomingTransferWorker>(_bob.Services);
        await transfer.ProcessNextAsync(CancellationToken.None); // Verhandlung gestartet (im Fake sofort abgeschlossen)

        var revoked = await ReadAsync(await alice.PostAsync($"/api/deliveries/{deliveryId}/revoke", null));
        Assert.Equal("Revoking", revoked.GetProperty("state").GetString());
        await _alice.RunDeliveryWorkerAsync();

        var after = (await ReadAsync(await alice.GetAsync("/api/shares"))).EnumerateArray().Single().GetProperty("deliveries")[0];
        Assert.Equal("Revoked", after.GetProperty("state").GetString());
        Assert.DoesNotContain($"drive-cd-{deliveryId:N}", _aliceEdc.ContractDefinitions.Keys);
        Assert.Single(_aliceEdc.Retired);
        // Asset mit Agreement bleibt als Grabstein stehen.
        Assert.Contains($"drive-d-{deliveryId:N}", _aliceEdc.Assets.Keys);

        await _bob.RunInboxAsync();
        var failed = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single();
        Assert.Equal("Failed", failed.GetProperty("state").GetString());
        Assert.Contains("verweigert", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unaccepted_offer_disappears_after_revocation_and_the_asset_is_removed()
    {
        var (alice, share, _) = await ShareFileAsync("Entwurf.docx", 512);
        var deliveryId = share.GetProperty("deliveries")[0].GetProperty("id").GetGuid();
        await _bob.PollAsync();

        await ReadAsync(await alice.PostAsync($"/api/deliveries/{deliveryId}/revoke", null));
        await _alice.RunDeliveryWorkerAsync();
        await _bob.PollAsync();

        var bob = await BobInboxAsync();
        Assert.Equal("Withdrawn", (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("state").GetString());
        Assert.DoesNotContain($"drive-d-{deliveryId:N}", _aliceEdc.Assets.Keys);
    }

    [Fact]
    public async Task Expired_deliveries_are_ended_and_the_content_endpoint_answers_gone()
    {
        var (alice, share, _) = await ShareFileAsync("Termin.txt", 100);
        var deliveryId = share.GetProperty("deliveries")[0].GetProperty("id").GetGuid();
        var token = _aliceEdc.Assets[$"drive-d-{deliveryId:N}"]["dataAddress"]!["authCode"]!.GetValue<string>();

        _alice.Clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, await ActivatorUtilities.CreateInstance<DeliveryWorker>(_alice.Services).ExpireDueAsync(CancellationToken.None));
        await _alice.RunDeliveryWorkerAsync();

        var listed = (await ReadAsync(await alice.GetAsync("/api/shares"))).EnumerateArray().Single().GetProperty("deliveries")[0];
        Assert.Equal("Expired", listed.GetProperty("state").GetString());
        // Ohne Agreement verschwindet das Asset ganz.
        Assert.DoesNotContain($"drive-d-{deliveryId:N}", _aliceEdc.Assets.Keys);
        var direct = _alice.CreateClient();
        direct.DefaultRequestHeaders.Add("X-Test-Local-Port", DriveApiFactory.ContentPort.ToString());
        direct.DefaultRequestHeaders.Add("X-Drive-Delivery-Token", token);
        direct.DefaultRequestHeaders.Add("Edc-Bpn", BobBpn);
        Assert.Equal(HttpStatusCode.Gone, (await direct.GetAsync($"/edc/deliveries/{deliveryId:N}")).StatusCode);
    }

    [Fact]
    public async Task Content_endpoint_rejects_a_valid_token_from_the_wrong_partner()
    {
        var (alice, share, _) = await ShareFileAsync("Geheim.txt", 64);
        var deliveryId = share.GetProperty("deliveries")[0].GetProperty("id").GetGuid();
        var token = _aliceEdc.Assets[$"drive-d-{deliveryId:N}"]["dataAddress"]!["authCode"]!.GetValue<string>();
        var direct = _alice.CreateClient();
        direct.DefaultRequestHeaders.Add("X-Test-Local-Port", DriveApiFactory.ContentPort.ToString());
        direct.DefaultRequestHeaders.Add("X-Drive-Delivery-Token", token);
        direct.DefaultRequestHeaders.Add("Edc-Bpn", "BPNL00000000WD04");
        direct.DefaultRequestHeaders.Add("Edc-Contract-Agreement-Id", "agr-fremd");

        var response = await direct.GetAsync($"/edc/deliveries/{deliveryId:N}");
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var retrievals = await ReadAsync(await alice.GetAsync($"/api/deliveries/{deliveryId}/retrievals"));
        Assert.Equal("WrongConsumer", retrievals[0].GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Sharing_needs_the_role_and_a_released_team_space()
    {
        var plain = await _alice.ClientForAsync("alice-plain", DrivePrincipal.RoleUser);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/partners/targets")).StatusCode);

        var admin = await _alice.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);
        var team = await ReadAsync(await admin.PostAsJsonAsync("/api/admin/spaces", new
        {
            name = "Projekt", acl = new[] { new { principalType = "Role", principalKey = DrivePrincipal.RoleUser, permission = "Write" } },
        }));
        var sharer = await AliceSharerAsync();
        var file = await UploadAsync(sharer, team.GetProperty("rootFolderId").GetGuid(), "Plan.pdf", "plan"u8.ToArray());
        var partnerId = (await ReadAsync(await sharer.GetAsync("/api/partners/targets"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        var request = new { fileIds = new[] { file.GetProperty("id").GetGuid() }, partnerIds = new[] { partnerId } };

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync("/api/shares", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sharer.PostAsJsonAsync("/api/shares", request)).StatusCode);

        await ReadAsync(await admin.PutAsJsonAsync($"/api/admin/spaces/{team.GetProperty("id").GetGuid()}", new
        {
            name = "Projekt", externalSharingAllowed = true,
            acl = new[] { new { principalType = "Role", principalKey = DrivePrincipal.RoleUser, permission = "Write" } },
        }));
        Assert.Equal(HttpStatusCode.Created, (await sharer.PostAsJsonAsync("/api/shares", request)).StatusCode);
    }

    [Fact]
    public async Task Shared_files_cannot_be_purged_until_the_share_is_revoked()
    {
        var (alice, share, _) = await ShareFileAsync("Vertrag.pdf", 128);
        var fileId = share.GetProperty("items")[0].GetProperty("fileEntryId").GetGuid();
        (await alice.DeleteAsync($"/api/items/{fileId}")).EnsureSuccessStatusCode();

        var purge = await alice.DeleteAsync($"/api/trash/{fileId}");
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);

        await ReadAsync(await alice.PostAsync($"/api/shares/{share.GetProperty("id").GetGuid()}/revoke", null));
        await _alice.RunDeliveryWorkerAsync();
        (await alice.DeleteAsync($"/api/trash/{fileId}")).EnsureSuccessStatusCode();
        // Die Freigabe bleibt mit Name und Prüfsumme in der Übersicht.
        var listed = (await ReadAsync(await alice.GetAsync("/api/shares"))).EnumerateArray().Single();
        Assert.Equal("Vertrag.pdf", listed.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Same_name_in_the_target_folder_gets_a_counter()
    {
        var bob = await BobInboxAsync();
        var bobRoot = await RootFolderAsync(bob);
        await UploadAsync(bob, bobRoot, "Bericht.txt", "lokal"u8.ToArray());
        await ShareFileAsync("Bericht.txt", 10);
        await _bob.PollAsync();
        var offerId = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("id").GetGuid();

        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = bobRoot }));
        await _bob.RunInboxAsync();

        var names = (await ReadAsync(await bob.GetAsync($"/api/folders/{bobRoot}/children"))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Contains("Bericht.txt", names);
        Assert.Contains("Bericht (2).txt", names);
    }

    [Fact]
    public async Task Offers_need_the_inbox_role_to_be_accepted()
    {
        await ShareFileAsync("Info.txt", 10);
        await _bob.PollAsync();
        var plain = await _bob.ClientForAsync("bob-plain", DrivePrincipal.RoleUser);
        var inbox = await BobInboxAsync();
        var offerId = (await ReadAsync(await inbox.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/inbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await plain.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = await RootFolderAsync(plain) })).StatusCode);
        // Fremder Zielordner: ohne Schreibrecht nicht sichtbar.
        Assert.Equal(HttpStatusCode.NotFound,
            (await inbox.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = await RootFolderAsync(plain) })).StatusCode);
        Assert.Equal("Declined", (await ReadAsync(await inbox.PostAsync($"/api/inbox/{offerId}/decline", null))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Receiver_quarantines_a_delivery_its_virus_scanner_flags()
    {
        // Absender ohne Virenscan (Alice), Empfänger mit Scanner (Bob): Bobs Prüfung muss greifen.
        _bob.Scanner.Enabled = true;
        var client = await AliceSharerAsync();
        var file = await UploadAsync(client, await RootFolderAsync(client), "rechnung.txt", "X5O!P%@AP EICAR test"u8.ToArray());
        var partnerId = (await ReadAsync(await client.GetAsync("/api/partners/targets"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await client.PostAsJsonAsync("/api/shares", new { fileIds = new[] { file.GetProperty("id").GetGuid() }, partnerIds = new[] { partnerId } }));
        await _alice.RunDeliveryWorkerAsync();
        await _bob.PollAsync();
        var bob = await BobInboxAsync();
        var offerId = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = await RootFolderAsync(bob) }));

        await _bob.RunInboxAsync();
        Assert.Equal("Scanning", (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("state").GetString());
        var scanner = ActivatorUtilities.CreateInstance<DataspaceDrive.Infrastructure.Scanning.ScanWorker>(_bob.Services);
        while (await scanner.ScanNextAsync(CancellationToken.None))
        {
        }
        await _bob.RunInboxAsync();

        var offer = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single();
        Assert.Equal("Quarantined", offer.GetProperty("state").GetString());
        Assert.Equal("Infected", offer.GetProperty("items")[0].GetProperty("scanStatus").GetString());
        var stored = offer.GetProperty("items")[0].GetProperty("fileEntryId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/files/{stored}/content")).StatusCode);
    }

    [Fact]
    public async Task Reconciliation_reports_open_selectors_and_cleans_orphans()
    {
        var (_, share, _) = await ShareFileAsync("Abgleich.txt", 32);
        var deliveryId = share.GetProperty("deliveries")[0].GetProperty("id").GetGuid().ToString("N");
        static System.Text.Json.Nodes.JsonObject Definition(string id, params System.Text.Json.Nodes.JsonObject[] selector) => new()
        {
            ["@id"] = id, ["accessPolicyId"] = "offen", ["contractPolicyId"] = "offen",
            ["assetsSelector"] = new System.Text.Json.Nodes.JsonArray(selector.Select(c => (System.Text.Json.Nodes.JsonNode)c).ToArray()),
        };
        static System.Text.Json.Nodes.JsonObject Criterion(string op, System.Text.Json.Nodes.JsonNode right) =>
            new() { ["operandLeft"] = "https://w3id.org/edc/v0.0.1/ns/id", ["operator"] = op, ["operandRight"] = right };
        _aliceEdc.ContractDefinitionBodies["contract-1"] = Definition("contract-1");
        _aliceEdc.ContractDefinitionBodies["contract-2"] = Definition("contract-2", Criterion("in", new System.Text.Json.Nodes.JsonArray("asset-1", "asset-2")));
        _aliceEdc.ContractDefinitionBodies["contract-3"] = Definition("contract-3", Criterion("=", $"drive-d-{deliveryId}"));
        _aliceEdc.ContractDefinitionBodies["drive-cd-0000000000000000000000000000dead"] = Definition("drive-cd-0000000000000000000000000000dead");
        _aliceEdc.ContractDefinitions["drive-cd-0000000000000000000000000000dead"] = ("drive-d-dead", "x");
        var admin = await _alice.ClientForAsync("admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);

        var status = await ReadAsync(await admin.PostAsync("/api/admin/edc/check", null));
        var findings = status.GetProperty("findings").EnumerateArray()
            .ToDictionary(f => f.GetProperty("objectId").GetString()!, f => f.GetProperty("severity").GetString());

        Assert.True(status.GetProperty("reachable").GetBoolean());
        Assert.Equal("Critical", findings["contract-1"]);
        Assert.False(findings.ContainsKey("contract-2"));
        Assert.Equal("Warning", findings["contract-3"]);
        Assert.Equal("Info", findings["drive-cd-0000000000000000000000000000dead"]);
        Assert.DoesNotContain("drive-cd-0000000000000000000000000000dead", _aliceEdc.ContractDefinitionBodies.Keys);
        Assert.False(findings.ContainsKey($"drive-cd-{deliveryId}"));

        // Fehlt die Contract Definition einer aktiven Zustellung, meldet der Abgleich das.
        _aliceEdc.ContractDefinitionBodies.Remove($"drive-cd-{deliveryId}");
        status = await ReadAsync(await admin.PostAsync("/api/admin/edc/check", null));
        Assert.Contains(status.GetProperty("findings").EnumerateArray(),
            f => f.GetProperty("objectId").GetString() == $"drive-cd-{deliveryId}" && f.GetProperty("severity").GetString() == "Warning");
    }

    [Fact]
    public async Task Shared_folder_arrives_with_its_structure()
    {
        var alice = await AliceSharerAsync();
        var root = await RootFolderAsync(alice);
        var begehung = (await ReadAsync(await alice.PostAsJsonAsync("/api/folders", new { parentId = root, name = "Begehung WEA 7" }))).GetProperty("id").GetGuid();
        var fotos = (await ReadAsync(await alice.PostAsJsonAsync("/api/folders", new { parentId = begehung, name = "Fotos" }))).GetProperty("id").GetGuid();
        var protocol = RandomNumberGenerator.GetBytes(300);
        var photo = RandomNumberGenerator.GetBytes(5000);
        await UploadAsync(alice, begehung, "Protokoll.txt", protocol);
        await UploadAsync(alice, fotos, "Rotor.jpg", photo);
        await UploadAsync(alice, fotos, "Turm.jpg", RandomNumberGenerator.GetBytes(10));
        var partnerId = (await ReadAsync(await alice.GetAsync("/api/partners/targets"))).EnumerateArray().Single().GetProperty("id").GetGuid();

        var share = await ReadAsync(await alice.PostAsJsonAsync("/api/shares", new { folderIds = new[] { begehung }, partnerIds = new[] { partnerId } }));
        Assert.Equal("Begehung WEA 7", share.GetProperty("title").GetString());
        Assert.Equal(["Begehung WEA 7", "Begehung WEA 7/Fotos", "Begehung WEA 7/Fotos"],
            share.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("relativePath").GetString()));
        await _alice.RunDeliveryWorkerAsync();

        await _bob.PollAsync();
        var bob = await BobInboxAsync();
        var bobRoot = await RootFolderAsync(bob);
        var offerId = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offerId}/accept", new { folderId = bobRoot }));
        await _bob.RunInboxAsync();

        Assert.Equal("Completed", (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single().GetProperty("state").GetString());
        async Task<JsonElement[]> Children(Guid folder) =>
            (await ReadAsync(await bob.GetAsync($"/api/folders/{folder}/children"))).GetProperty("items").EnumerateArray().ToArray();
        var top = (await Children(bobRoot)).Single(i => i.GetProperty("name").GetString() == "Begehung WEA 7");
        var inside = await Children(top.GetProperty("id").GetGuid());
        var protocolCopy = inside.Single(i => i.GetProperty("name").GetString() == "Protokoll.txt");
        var fotosCopy = inside.Single(i => i.GetProperty("name").GetString() == "Fotos" && i.GetProperty("isDirectory").GetBoolean());
        var photos = await Children(fotosCopy.GetProperty("id").GetGuid());
        Assert.Equal(["Rotor.jpg", "Turm.jpg"], photos.Select(p => p.GetProperty("name").GetString()).Order());
        var photoCopy = photos.Single(p => p.GetProperty("name").GetString() == "Rotor.jpg");
        Assert.Equal(SHA256.HashData(photo), SHA256.HashData(await (await bob.GetAsync($"/api/files/{photoCopy.GetProperty("id").GetGuid()}/content")).Content.ReadAsByteArrayAsync()));
        Assert.Equal(SHA256.HashData(protocol), SHA256.HashData(await (await bob.GetAsync($"/api/files/{protocolCopy.GetProperty("id").GetGuid()}/content")).Content.ReadAsByteArrayAsync()));
    }

    [Theory]
    [InlineData("../a/./b//c", "a/b/c")]
    [InlineData("a:b/c*d", "a_b/c_d")]
    [InlineData("..", null)]
    [InlineData(null, null)]
    public void Folder_paths_from_partners_are_sanitized(string? path, string? expected) =>
        Assert.Equal(expected, InboxPoller.SafePath(path));

    private async Task<Guid> AcceptOnlyNewOfferAsync(HttpClient bob, Guid folderId)
    {
        await _bob.PollAsync();
        var offer = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single(o => o.GetProperty("state").GetString() == "New");
        await ReadAsync(await bob.PostAsJsonAsync($"/api/inbox/{offer.GetProperty("id").GetGuid()}/accept", new { folderId }));
        await _bob.RunInboxAsync();
        return offer.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Updated_share_becomes_a_new_version_of_the_received_file()
    {
        var alice = await AliceSharerAsync();
        var aliceRoot = await RootFolderAsync(alice);
        await UploadAsync(alice, aliceRoot, "Pflichtenheft.pdf", "Version eins"u8.ToArray());
        var fileId = (await ReadAsync(await alice.GetAsync($"/api/folders/{aliceRoot}/children"))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var partnerId = (await ReadAsync(await alice.GetAsync("/api/partners/targets"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        var first = await ReadAsync(await alice.PostAsJsonAsync("/api/shares", new { fileIds = new[] { fileId }, partnerIds = new[] { partnerId } }));
        await _alice.RunDeliveryWorkerAsync();
        var bob = await BobInboxAsync();
        var bobRoot = await RootFolderAsync(bob);
        await AcceptOnlyNewOfferAsync(bob, bobRoot);

        await UploadAsync(alice, aliceRoot, "Pflichtenheft.pdf", "Version zwei"u8.ToArray());
        var listed = (await ReadAsync(await alice.GetAsync("/api/shares"))).EnumerateArray().Single();
        Assert.True(listed.GetProperty("hasNewerVersions").GetBoolean());
        var second = await ReadAsync(await alice.PostAsJsonAsync($"/api/shares/{first.GetProperty("id").GetGuid()}/update", new { message = "Neue Fassung" }));
        Assert.Equal(2, second.GetProperty("revision").GetInt32());
        Assert.Equal(first.GetProperty("lineageId").GetGuid(), second.GetProperty("lineageId").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict,
            (await alice.PostAsJsonAsync($"/api/shares/{first.GetProperty("id").GetGuid()}/update", new { })).StatusCode);
        await _alice.RunDeliveryWorkerAsync();
        var shares = (await ReadAsync(await alice.GetAsync("/api/shares"))).EnumerateArray().ToList();
        Assert.Equal("Revoked", shares.Single(s => s.GetProperty("revision").GetInt32() == 1).GetProperty("deliveries")[0].GetProperty("state").GetString());
        Assert.Equal("Active", shares.Single(s => s.GetProperty("revision").GetInt32() == 2).GetProperty("deliveries")[0].GetProperty("state").GetString());

        await _bob.PollAsync();
        var update = (await ReadAsync(await bob.GetAsync("/api/inbox"))).EnumerateArray().Single(o => o.GetProperty("state").GetString() == "New");
        Assert.Equal(2, update.GetProperty("revision").GetInt32());
        Assert.Equal(bobRoot, update.GetProperty("previousTargetFolderId").GetGuid());
        await AcceptOnlyNewOfferAsync(bob, bobRoot);

        var copies = (await ReadAsync(await bob.GetAsync($"/api/folders/{bobRoot}/children"))).GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("name").GetString()!.StartsWith("Pflichtenheft", StringComparison.Ordinal)).ToList();
        var copy = Assert.Single(copies);
        var versions = await ReadAsync(await bob.GetAsync($"/api/files/{copy.GetProperty("id").GetGuid()}/versions"));
        Assert.Equal(2, versions.GetArrayLength());
        Assert.Equal("Version zwei", await (await bob.GetAsync($"/api/files/{copy.GetProperty("id").GetGuid()}/content")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Updating_a_folder_share_delivers_new_files_into_the_existing_structure()
    {
        var alice = await AliceSharerAsync();
        var aliceRoot = await RootFolderAsync(alice);
        var folder = (await ReadAsync(await alice.PostAsJsonAsync("/api/folders", new { parentId = aliceRoot, name = "Begehung" }))).GetProperty("id").GetGuid();
        await UploadAsync(alice, folder, "Alt.txt", "alt"u8.ToArray());
        var partnerId = (await ReadAsync(await alice.GetAsync("/api/partners/targets"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        var first = await ReadAsync(await alice.PostAsJsonAsync("/api/shares", new { folderIds = new[] { folder }, partnerIds = new[] { partnerId } }));
        await _alice.RunDeliveryWorkerAsync();
        var bob = await BobInboxAsync();
        var bobRoot = await RootFolderAsync(bob);
        await AcceptOnlyNewOfferAsync(bob, bobRoot);

        await UploadAsync(alice, folder, "Neu.txt", "neu"u8.ToArray());
        await ReadAsync(await alice.PostAsJsonAsync($"/api/shares/{first.GetProperty("id").GetGuid()}/update", new { }));
        await _alice.RunDeliveryWorkerAsync();
        await AcceptOnlyNewOfferAsync(bob, bobRoot);

        var top = (await ReadAsync(await bob.GetAsync($"/api/folders/{bobRoot}/children"))).GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("isDirectory").GetBoolean()).ToList();
        var begehung = Assert.Single(top);
        var names = (await ReadAsync(await bob.GetAsync($"/api/folders/{begehung.GetProperty("id").GetGuid()}/children"))).GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("name").GetString()).Order().ToList();
        Assert.Equal(["Alt.txt", "Neu.txt"], names);
    }
}
