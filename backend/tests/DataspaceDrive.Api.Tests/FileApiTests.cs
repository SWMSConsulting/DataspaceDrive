using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DataspaceDrive.Api.Tests.ApiTestHelpers;

namespace DataspaceDrive.Api.Tests;

public sealed class FileApiTests : IClassFixture<DriveApiFactory>
{
    private readonly DriveApiFactory _factory;

    public FileApiTests(DriveApiFactory factory) => _factory = factory;

    [Fact]
    public async Task First_request_provisions_user_with_personal_space()
    {
        var client = await _factory.ClientForAsync("provision-user");

        var spaces = await ReadAsync(await client.GetAsync("/api/spaces"));

        var personal = Assert.Single(spaces.EnumerateArray());
        Assert.Equal("Meine Dateien", personal.GetProperty("name").GetString());
        Assert.Equal("Manage", personal.GetProperty("permission").GetString());
    }

    [Fact]
    public async Task Me_reports_roles_and_capabilities()
    {
        var client = await _factory.ClientForAsync("role-user", "drive-user", "drive-external-share");

        var me = await ReadAsync(await client.GetAsync("/api/me"));

        Assert.True(me.GetProperty("canShareExternally").GetBoolean());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Users_without_drive_role_are_forbidden()
    {
        var client = _factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Test-User", "no-role");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "something-else");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Changing_requests_need_the_xsrf_header()
    {
        var client = await _factory.ClientForAsync("xsrf-user");
        var root = await RootFolderAsync(client);
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");

        var response = await client.PostAsJsonAsync("/api/folders", new { parentId = root, name = "Ohne Token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Folder_lifecycle_with_conflicts_trash_restore_and_purge()
    {
        var client = await _factory.ClientForAsync("folder-user");
        var root = await RootFolderAsync(client);

        var reports = await ReadAsync(await client.PostAsJsonAsync("/api/folders", new { parentId = root, name = "Berichte" }));
        var reportsId = reports.GetProperty("id").GetGuid();
        var archive = await ReadAsync(await client.PostAsJsonAsync("/api/folders", new { parentId = root, name = "Archiv" }));
        var archiveId = archive.GetProperty("id").GetGuid();

        var duplicate = await client.PostAsJsonAsync("/api/folders", new { parentId = root, name = "berichte" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var invalid = await client.PostAsJsonAsync("/api/folders", new { parentId = root, name = "a/b" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await ReadAsync(await client.PatchAsJsonAsync($"/api/items/{reportsId}", new { name = "Prüfberichte" }));
        await ReadAsync(await client.PostAsJsonAsync($"/api/items/{reportsId}/move", new { targetFolderId = archiveId }));
        var intoItself = await client.PostAsJsonAsync($"/api/items/{archiveId}/move", new { targetFolderId = reportsId });
        Assert.Equal(HttpStatusCode.BadRequest, intoItself.StatusCode);

        var listing = await ReadAsync(await client.GetAsync($"/api/folders/{archiveId}/children"));
        Assert.Equal("Prüfberichte", Assert.Single(listing.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(["Meine Dateien", "Archiv"], listing.GetProperty("path").EnumerateArray().Select(p => p.GetProperty("name").GetString()));

        await UploadAsync(client, reportsId, "WEA17.pdf", "inhalt"u8.ToArray());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/items/{archiveId}")).StatusCode);
        var rootListing = await ReadAsync(await client.GetAsync($"/api/folders/{root}/children"));
        Assert.Empty(rootListing.GetProperty("items").EnumerateArray());

        var trash = await ReadAsync(await client.GetAsync("/api/trash"));
        var trashed = Assert.Single(trash.EnumerateArray());
        Assert.Equal("Archiv", trashed.GetProperty("name").GetString());

        // Ein neuer Ordner gleichen Namens zwingt die Wiederherstellung auf einen freien Namen.
        await ReadAsync(await client.PostAsJsonAsync("/api/folders", new { parentId = root, name = "Archiv" }));
        var restored = await ReadAsync(await client.PostAsync($"/api/trash/{archiveId}/restore", null));
        Assert.Equal("Archiv (2)", restored.GetProperty("name").GetString());
        var nested = await ReadAsync(await client.GetAsync($"/api/folders/{reportsId}/children"));
        Assert.Equal("WEA17.pdf", Assert.Single(nested.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/items/{archiveId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/trash/{archiveId}")).StatusCode);
        Assert.Empty((await ReadAsync(await client.GetAsync("/api/trash"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/folders/{reportsId}/children")).StatusCode);
    }

    [Fact]
    public async Task Chunked_upload_download_range_and_versions()
    {
        var client = await _factory.ClientForAsync("upload-user");
        var root = await RootFolderAsync(client);
        var content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 12345); // drei Teilstücke à 1 MiB

        var item = await UploadAsync(client, root, "Messdaten.bin", content);
        Assert.Equal(content.Length, item.GetProperty("size").GetInt64());
        Assert.Equal("NotScanned", item.GetProperty("scanStatus").GetString());
        var fileId = item.GetProperty("id").GetGuid();

        var download = await client.GetAsync($"/api/files/{fileId}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());

        var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{fileId}/content");
        ranged.Headers.Range = new RangeHeaderValue(1000, 1999);
        var partial = await client.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(content[1000..2000], await partial.Content.ReadAsByteArrayAsync());

        var second = await UploadAsync(client, root, "messdaten.BIN", "neu"u8.ToArray());
        Assert.Equal(fileId, second.GetProperty("id").GetGuid());
        Assert.Equal(2, second.GetProperty("versionNo").GetInt32());
        var versions = await ReadAsync(await client.GetAsync($"/api/files/{fileId}/versions"));
        Assert.Equal([2, 1], versions.EnumerateArray().Select(v => v.GetProperty("versionNo").GetInt32()));
        var first = await client.GetAsync($"/api/files/{fileId}/content?version=1");
        Assert.Equal(content, await first.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Repeated_chunks_are_ignored_and_out_of_order_chunks_rejected()
    {
        var client = await _factory.ClientForAsync("chunk-user");
        var root = await RootFolderAsync(client);
        var content = RandomNumberGenerator.GetBytes(1024 * 1024 + 10);
        var ticket = await ReadAsync(await client.PostAsJsonAsync("/api/uploads", new { folderId = root, fileName = "a.bin", size = content.Length }));
        var uploadId = ticket.GetProperty("uploadId").GetGuid();

        ByteArrayContent Part(int offset, int length) => new(content, offset, length);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync($"/api/uploads/{uploadId}/chunks/1", Part(0, 10))).StatusCode);
        (await client.PutAsync($"/api/uploads/{uploadId}/chunks/0", Part(0, 1024 * 1024))).EnsureSuccessStatusCode();
        (await client.PutAsync($"/api/uploads/{uploadId}/chunks/0", Part(0, 1024 * 1024))).EnsureSuccessStatusCode();
        var incomplete = await client.PostAsJsonAsync($"/api/uploads/{uploadId}/complete", new { contentType = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, incomplete.StatusCode);
        (await client.PutAsync($"/api/uploads/{uploadId}/chunks/1", Part(1024 * 1024, 10))).EnsureSuccessStatusCode();

        var item = await ReadAsync(await client.PostAsJsonAsync($"/api/uploads/{uploadId}/complete", new { contentType = (string?)null }));
        var download = await client.GetAsync($"/api/files/{item.GetProperty("id").GetGuid()}/content");
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Content_type_falls_back_to_the_file_extension()
    {
        var client = await _factory.ClientForAsync("type-user");
        var root = await RootFolderAsync(client);

        var item = await UploadAsync(client, root, "Plan.pdf", "%PDF-1.7"u8.ToArray());

        Assert.Equal("application/pdf", item.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Other_users_cannot_see_a_personal_space()
    {
        var alice = await _factory.ClientForAsync("alice");
        var mallory = await _factory.ClientForAsync("mallory");
        var aliceRoot = await RootFolderAsync(alice);
        var file = await UploadAsync(alice, aliceRoot, "geheim.txt", "nur für Alice"u8.ToArray());

        Assert.Equal(HttpStatusCode.NotFound, (await mallory.GetAsync($"/api/folders/{aliceRoot}/children")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.GetAsync($"/api/files/{file.GetProperty("id").GetGuid()}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await mallory.PostAsJsonAsync("/api/folders", new { parentId = aliceRoot, name = "Einbruch" })).StatusCode);
        Assert.Empty((await ReadAsync(await mallory.GetAsync("/api/search?q=geheim"))).EnumerateArray());
    }

    [Fact]
    public async Task Search_finds_files_and_folders_by_name_part()
    {
        var client = await _factory.ClientForAsync("search-user");
        var root = await RootFolderAsync(client);
        await ReadAsync(await client.PostAsJsonAsync("/api/folders", new { parentId = root, name = "Wartung 2026" }));
        await UploadAsync(client, root, "Wartungsprotokoll.docx", "x"u8.ToArray());

        var hits = await ReadAsync(await client.GetAsync("/api/search?q=wartung"));

        Assert.Equal(2, hits.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/search?q=w")).StatusCode);
    }

    [Fact]
    public async Task Downloads_wait_for_the_virus_scan_and_infected_files_stay_blocked()
    {
        _factory.Scanner.Enabled = true;
        try
        {
            var client = await _factory.ClientForAsync("scan-user");
            var root = await RootFolderAsync(client);
            var clean = await UploadAsync(client, root, "sauber.txt", "harmlos"u8.ToArray());
            var infected = await UploadAsync(client, root, "eicar.txt", "X5O!P%@AP EICAR test"u8.ToArray());
            Assert.Equal("Pending", clean.GetProperty("scanStatus").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/files/{clean.GetProperty("id").GetGuid()}/content")).StatusCode);

            var worker = ActivatorUtilities.CreateInstance<ScanWorker>(_factory.Services);
            while (await worker.ScanNextAsync(CancellationToken.None))
            {
            }

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/files/{clean.GetProperty("id").GetGuid()}/content")).StatusCode);
            var blocked = await client.GetAsync($"/api/files/{infected.GetProperty("id").GetGuid()}/content");
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Contains("Eicar-Test-Signature", await blocked.Content.ReadAsStringAsync());

            _factory.Scanner.MaxBytes = 4;
            Assert.Equal(4, (await ReadAsync(await client.GetAsync("/api/config"))).GetProperty("maxUploadBytes").GetInt64());
            var tooLarge = await client.PostAsJsonAsync("/api/uploads", new { folderId = root, fileName = "gross.bin", size = 5 });
            Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
            Assert.Contains("Virenscan", await tooLarge.Content.ReadAsStringAsync());
        }
        finally
        {
            _factory.Scanner.Enabled = false;
            _factory.Scanner.MaxBytes = null;
        }
    }

    [Fact]
    public async Task Expired_uploads_are_cleaned_up_with_their_chunks()
    {
        var client = await _factory.ClientForAsync("cleanup-user");
        var root = await RootFolderAsync(client);
        var ticket = await ReadAsync(await client.PostAsJsonAsync("/api/uploads", new { folderId = root, fileName = "halb.bin", size = 10 }));
        var uploadId = ticket.GetProperty("uploadId").GetGuid();
        var part = new ByteArrayContent(new byte[4]);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        (await client.PutAsync($"/api/uploads/{uploadId}/chunks/0", part)).EnsureSuccessStatusCode();
        var staged = Path.Combine(_factory.DataRoot, "uploads", uploadId.ToString("N"));
        Assert.True(File.Exists(staged));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
            (await db.UploadSessions.SingleAsync(u => u.Id == uploadId)).ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var removed = await ActivatorUtilities.CreateInstance<UploadCleanupWorker>(_factory.Services).CleanupAsync(CancellationToken.None);

        Assert.True(removed >= 1);
        Assert.False(File.Exists(staged));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/api/uploads/{uploadId}/chunks/1", part)).StatusCode);
    }
}
