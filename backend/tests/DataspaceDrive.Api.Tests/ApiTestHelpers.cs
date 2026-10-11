using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DataspaceDrive.Api.Tests;

internal static class ApiTestHelpers
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    public static async Task<Guid> RootFolderAsync(HttpClient client)
    {
        var spaces = await ReadAsync(await client.GetAsync("/api/spaces"));
        return spaces.EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "Personal").GetProperty("rootFolderId").GetGuid();
    }

    public static async Task<JsonElement> UploadAsync(HttpClient client, Guid folderId, string name, byte[] content, string? contentType = null)
    {
        var ticket = await ReadAsync(await client.PostAsJsonAsync("/api/uploads", new { folderId, fileName = name, size = content.Length }));
        var uploadId = ticket.GetProperty("uploadId").GetGuid();
        var chunkSize = ticket.GetProperty("chunkSize").GetInt32();
        var index = 0;
        for (var offset = 0; offset < content.Length || (content.Length == 0 && index == 0); offset += chunkSize, index++)
        {
            var part = new ByteArrayContent(content, offset, Math.Min(chunkSize, content.Length - offset));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            (await client.PutAsync($"/api/uploads/{uploadId}/chunks/{index}", part)).EnsureSuccessStatusCode();
            if (content.Length == 0)
                break;
        }
        return await ReadAsync(await client.PostAsJsonAsync($"/api/uploads/{uploadId}/complete", new { contentType }));
    }
}
