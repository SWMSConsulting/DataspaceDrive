using System.Buffers;
using DataspaceDrive.Core;
using DataspaceDrive.Core.Storage;

namespace DataspaceDrive.Infrastructure.Storage;

/// <summary>Upload-Ablage unter <c>&lt;Datenwurzel&gt;/uploads</c>, auf demselben Dateisystem wie die Blobs.</summary>
public sealed class FileSystemUploadStaging(FileSystemBlobStore blobs) : IUploadStaging
{
    private const int BufferSize = 81920;

    private readonly string _directory = Path.Combine(blobs.Root, "uploads");

    public async Task<long> WriteChunkAsync(Guid uploadId, long offset, Stream chunk, long maxBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        await using var file = new FileStream(PathFor(uploadId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, BufferSize,
            FileOptions.Asynchronous);
        if (file.Length < offset)
            throw new ConflictException("Das Teilstück passt nicht an die bisherige Position des Uploads.");
        // Reste eines abgebrochenen Versuchs abschneiden, dann an der festen Position schreiben.
        file.SetLength(offset);
        file.Position = offset;

        long written = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await chunk.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                written += read;
                if (written > maxBytes)
                    throw new InvalidInputException("Das Teilstück ist größer als erlaubt.");
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        await file.FlushAsync(ct);
        return written;
    }

    public Task<BlobInfo> PromoteAsync(Guid uploadId, string blobKey, CancellationToken ct) =>
        blobs.ImportFileAsync(blobKey, PathFor(uploadId), ct);

    public Task DiscardAsync(Guid uploadId, CancellationToken ct)
    {
        File.Delete(PathFor(uploadId));
        return Task.CompletedTask;
    }

    private string PathFor(Guid uploadId) => Path.Combine(_directory, uploadId.ToString("N"));
}
