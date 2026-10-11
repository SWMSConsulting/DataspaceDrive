using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DataspaceDrive.Core.Access;
using static DataspaceDrive.Api.Tests.ApiTestHelpers;

namespace DataspaceDrive.Api.Tests;

public sealed class TeamSpaceApiTests(DriveApiFactory factory) : IClassFixture<DriveApiFactory>
{
    private Task<HttpClient> AdminAsync() => factory.ClientForAsync("ts-admin", DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin);

    private static object Acl(string type, string key, string permission) =>
        new { principalType = type, principalKey = key, permission };

    private static async Task<JsonElement> CreateAsync(HttpClient admin, string name, params object[] acl) =>
        await ReadAsync(await admin.PostAsJsonAsync("/api/admin/spaces", new { name, acl }));

    private static async Task<JsonElement?> FindSpaceAsync(HttpClient client, string name)
    {
        var spaces = await ReadAsync(await client.GetAsync("/api/spaces"));
        return spaces.EnumerateArray().Where(s => s.GetProperty("name").GetString() == name).Select(s => (JsonElement?)s).SingleOrDefault();
    }

    [Fact]
    public async Task Members_work_in_a_team_space_according_to_their_rights()
    {
        var admin = await AdminAsync();
        var writer = await factory.ClientForAsync("ts-writer", DrivePrincipal.RoleUser, "team-nord");
        var reader = await factory.ClientForAsync("ts-reader", DrivePrincipal.RoleUser, "team-nord-lesen");
        var outsider = await factory.ClientForAsync("ts-outsider", DrivePrincipal.RoleUser);

        var created = await CreateAsync(admin, "Projekt Nord", Acl("Role", "team-nord", "Write"), Acl("Role", "team-nord-lesen", "Read"));
        var root = created.GetProperty("rootFolderId").GetGuid();

        Assert.Equal("Write", (await FindSpaceAsync(writer, "Projekt Nord"))!.Value.GetProperty("permission").GetString());
        var file = await UploadAsync(writer, root, "Pflichtenheft.pdf", "%PDF-1.7 Inhalt"u8.ToArray());
        Assert.Equal(HttpStatusCode.Created, (await writer.PostAsJsonAsync("/api/folders", new { parentId = root, name = "Fotos" })).StatusCode);

        Assert.Equal("Read", (await FindSpaceAsync(reader, "Projekt Nord"))!.Value.GetProperty("permission").GetString());
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync($"/api/files/{file.GetProperty("id").GetGuid()}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.PostAsJsonAsync("/api/uploads", new { folderId = root, fileName = "neu.txt", size = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync($"/api/items/{file.GetProperty("id").GetGuid()}")).StatusCode);

        // Weder Außenstehende noch Administratoren ohne ACL-Eintrag sehen Inhalte.
        Assert.Null(await FindSpaceAsync(outsider, "Projekt Nord"));
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/folders/{root}/children")).StatusCode);
        Assert.Null(await FindSpaceAsync(admin, "Projekt Nord"));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/folders/{root}/children")).StatusCode);

        var listed = (await ReadAsync(await admin.GetAsync("/api/admin/spaces"))).EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == "Projekt Nord");
        Assert.Equal(1, listed.GetProperty("fileCount").GetInt32());
        Assert.Equal("%PDF-1.7 Inhalt"u8.Length, listed.GetProperty("storedBytes").GetInt64());
        Assert.Equal(2, listed.GetProperty("acl").GetArrayLength());
    }

    [Fact]
    public async Task Only_admins_manage_team_spaces()
    {
        var user = await factory.ClientForAsync("ts-plain-user", DrivePrincipal.RoleUser);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/spaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/admin/spaces", new { name = "Heimlich" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/admin/users")).StatusCode);
    }

    [Fact]
    public async Task Names_and_rights_are_validated()
    {
        var admin = await AdminAsync();
        await CreateAsync(admin, "Einkauf");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/spaces", new { name = "EINKAUF" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/spaces", new { name = "Meine Dateien" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/spaces", new { name = "a/b" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/spaces",
            new { name = "Verwalten", acl = new[] { Acl("Role", "x", "Manage") } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/spaces",
            new { name = "Fremd", acl = new[] { Acl("User", Guid.NewGuid().ToString(), "Read") } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/spaces",
            new { name = "Doppelt", acl = new[] { Acl("Group", "g", "Read"), Acl("Group", "g", "Write") } })).StatusCode);
    }

    [Fact]
    public async Task Renaming_and_user_entries_take_effect_immediately()
    {
        var admin = await AdminAsync();
        var member = await factory.ClientForAsync("ts-member", DrivePrincipal.RoleUser);
        var memberId = (await ReadAsync(await member.GetAsync("/api/me"))).GetProperty("id").GetGuid();
        var users = await ReadAsync(await admin.GetAsync("/api/admin/users"));
        Assert.Contains(users.EnumerateArray(), u => u.GetProperty("id").GetGuid() == memberId);

        var created = await CreateAsync(admin, "Wartung", Acl("User", memberId.ToString(), "Write"));
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("ts-member", created.GetProperty("acl")[0].GetProperty("displayName").GetString());
        Assert.Equal("Write", (await FindSpaceAsync(member, "Wartung"))!.Value.GetProperty("permission").GetString());

        await ReadAsync(await admin.PutAsJsonAsync($"/api/admin/spaces/{id}",
            new { name = "Wartung 2027", acl = new[] { Acl("User", memberId.ToString(), "Read") } }));

        Assert.Null(await FindSpaceAsync(member, "Wartung"));
        var renamed = (await FindSpaceAsync(member, "Wartung 2027"))!.Value;
        Assert.Equal("Read", renamed.GetProperty("permission").GetString());
        var listing = await ReadAsync(await member.GetAsync($"/api/folders/{renamed.GetProperty("rootFolderId").GetGuid()}/children"));
        Assert.Equal("Wartung 2027", listing.GetProperty("path")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Only_empty_spaces_can_be_deleted()
    {
        var admin = await AdminAsync();
        var member = await factory.ClientForAsync("ts-cleaner", DrivePrincipal.RoleUser, "team-archiv");
        var created = await CreateAsync(admin, "Archiv", Acl("Role", "team-archiv", "Write"));
        var id = created.GetProperty("id").GetGuid();
        var file = await UploadAsync(member, created.GetProperty("rootFolderId").GetGuid(), "alt.txt", "alt"u8.ToArray());
        var fileId = file.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/spaces/{id}")).StatusCode);
        (await member.DeleteAsync($"/api/items/{fileId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/spaces/{id}")).StatusCode);
        (await member.DeleteAsync($"/api/trash/{fileId}")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/spaces/{id}")).StatusCode);
        Assert.Null(await FindSpaceAsync(member, "Archiv"));
        Assert.DoesNotContain((await ReadAsync(await admin.GetAsync("/api/admin/spaces"))).EnumerateArray(),
            s => s.GetProperty("id").GetGuid() == id);
    }
}
