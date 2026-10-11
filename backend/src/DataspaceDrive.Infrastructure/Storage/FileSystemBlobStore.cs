using System.Buffers;
using System.Security.Cryptography;
using DataspaceDrive.Core.Storage;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Infrastructure.Storage;

public sealed class FileSystemBlobStoreOptions
{
    /// <summary>Datenwurzel, im Cluster ein verschlüsseltes PVC; darunter liegen <c>blobs/</c> und <c>uploads/</c>.</summary>
    public string RootPath { get; set; } = "/data";
}

/// <summary>
/// Blob-Ablage im Dateisystem. Geschrieben wird erst in eine temporäre Datei im Zielverzeichnis und dann
/// atomar umbenannt, damit ein abgebrochener Upload nie als vollständiger Blob sichtbar wird.
/// </summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private const int BufferSize = 81920;

    private readonly string _root;

    public FileSystemBlobStore(IOptions<FileSystemBlobStoreOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_root);
    }

    internal string Root => _root;

    public async Task<BlobInfo> WriteAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (File.Exists(path))
            throw new IOException($"Blob '{key}' existiert bereits; Blobs sind unveränderlich.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            BlobInfo info;
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                             FileOptions.Asynchronous))
            {
                info = await CopyAndHashAsync(key, content, target, ct);
                await target.FlushAsync(ct);
            }
            File.Move(temp, path, overwrite: false);
            return info;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Übernimmt eine fertige Datei aus der Upload-Ablage desselben Dateisystems: ein Lesedurchgang für die
    /// Prüfsumme, danach Umbenennen statt Kopieren.
    /// </summary>
    internal async Task<BlobInfo> ImportFileAsync(string key, string sourcePath, CancellationToken ct)
    {
        var path = Resolve(key);
        if (File.Exists(path))
            throw new IOException($"Blob '{key}' existiert bereits; Blobs sind unveränderlich.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BlobInfo info;
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            info = await CopyAndHashAsync(key, source, Stream.Null, ct);
        }
        File.Move(sourcePath, path, overwrite: false);
        return info;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan));

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(File.Exists(Resolve(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        File.Delete(Resolve(key));
        return Task.CompletedTask;
    }

    private static async Task<BlobInfo> CopyAndHashAsync(string key, Stream source, Stream target, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                length += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new BlobInfo(key, length, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    private string Resolve(string key)
    {
        BlobKey.EnsureValid(key);
        var full = Path.GetFullPath(Path.Combine(_root, key));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Blob-Schlüssel '{key}' zeigt aus dem Ablageverzeichnis heraus.", nameof(key));
        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Aufräumen nach einem Fehler darf den ursprünglichen Fehler nicht überdecken.
        }
    }
}
