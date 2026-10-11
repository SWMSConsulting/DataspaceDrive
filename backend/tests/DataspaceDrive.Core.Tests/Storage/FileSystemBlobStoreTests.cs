using System.Security.Cryptography;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Core.Tests.Storage;

public sealed class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "drive-blobs-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystemBlobStore _store;

    public FileSystemBlobStoreTests() =>
        _store = new FileSystemBlobStore(Options.Create(new FileSystemBlobStoreOptions { RootPath = _root }));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Write_computes_length_and_sha256_and_read_returns_same_bytes()
    {
        var data = RandomNumberGenerator.GetBytes(300_000);

        var info = await _store.WriteAsync("blobs/ab/v1", new MemoryStream(data));

        Assert.Equal(data.Length, info.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), info.Sha256);
        await using var read = await _store.OpenReadAsync("blobs/ab/v1");
        var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        Assert.Equal(data, copy.ToArray());
    }

    [Fact]
    public async Task Blobs_are_immutable()
    {
        await _store.WriteAsync("blobs/x", new MemoryStream([1, 2, 3]));

        await Assert.ThrowsAsync<IOException>(() => _store.WriteAsync("blobs/x", new MemoryStream([4])));
    }

    [Fact]
    public async Task Failed_write_leaves_no_blob_and_no_temp_file()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.WriteAsync("blobs/broken", new FailingStream()));

        Assert.False(await _store.ExistsAsync("blobs/broken"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("blobs/../../escape")]
    [InlineData("Blobs/Upper")]
    [InlineData("blobs//double")]
    [InlineData("blobs/trailing/")]
    public async Task Invalid_keys_are_rejected(string key) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _store.WriteAsync(key, new MemoryStream([1])));

    [Theory]
    [InlineData("blobs/0192f3a1/v1", true)]
    [InlineData("quarantine/offer_1/item-2", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("a/./b", false)]
    [InlineData("a/b.c", false)]
    public void BlobKey_validation(string key, bool valid) => Assert.Equal(valid, BlobKey.IsValid(key));

    private sealed class FailingStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            _reads++ == 0 ? count : throw new InvalidOperationException("Verbindung abgebrochen");
    }
}
