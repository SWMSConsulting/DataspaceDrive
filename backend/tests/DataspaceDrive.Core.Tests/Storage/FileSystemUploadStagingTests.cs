using System.Security.Cryptography;
using DataspaceDrive.Core;
using DataspaceDrive.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Core.Tests.Storage;

public sealed class FileSystemUploadStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "drive-staging-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemBlobStore _blobs;
    private readonly FileSystemUploadStaging _staging;

    public FileSystemUploadStagingTests()
    {
        _blobs = new FileSystemBlobStore(Options.Create(new FileSystemBlobStoreOptions { RootPath = _root }));
        _staging = new FileSystemUploadStaging(_blobs);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Chunks_are_assembled_and_promoted_with_hash_without_copying()
    {
        var id = Guid.NewGuid();
        var content = RandomNumberGenerator.GetBytes(3000);

        await _staging.WriteChunkAsync(id, 0, new MemoryStream(content[..1000]), 1000, CancellationToken.None);
        await _staging.WriteChunkAsync(id, 1000, new MemoryStream(content[1000..]), 2000, CancellationToken.None);
        var blob = await _staging.PromoteAsync(id, "blobs/v1", CancellationToken.None);

        Assert.Equal(3000, blob.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), blob.Sha256);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "uploads")));
        await using var read = await _blobs.OpenReadAsync("blobs/v1");
        var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());
    }

    [Fact]
    public async Task Retry_after_an_aborted_chunk_overwrites_the_partial_rest()
    {
        var id = Guid.NewGuid();
        await _staging.WriteChunkAsync(id, 0, new MemoryStream("AAAA"u8.ToArray()), 4, CancellationToken.None);
        // Ein abgebrochener Versuch hinterlässt mehr Bytes, als die Datenbank als empfangen kennt.
        await Assert.ThrowsAsync<InvalidInputException>(() =>
            _staging.WriteChunkAsync(id, 4, new MemoryStream("BBBBBBBB"u8.ToArray()), 4, CancellationToken.None));

        await _staging.WriteChunkAsync(id, 4, new MemoryStream("CCCC"u8.ToArray()), 4, CancellationToken.None);
        var blob = await _staging.PromoteAsync(id, "blobs/v2", CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("AAAACCCC"u8.ToArray())), blob.Sha256);
    }

    [Fact]
    public async Task A_gap_before_the_chunk_is_rejected()
    {
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ConflictException>(() =>
            _staging.WriteChunkAsync(id, 10, new MemoryStream("x"u8.ToArray()), 1, CancellationToken.None));
    }

    [Fact]
    public async Task Discard_removes_the_staged_file()
    {
        var id = Guid.NewGuid();
        await _staging.WriteChunkAsync(id, 0, new MemoryStream("x"u8.ToArray()), 1, CancellationToken.None);

        await _staging.DiscardAsync(id, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "uploads")));
    }
}
