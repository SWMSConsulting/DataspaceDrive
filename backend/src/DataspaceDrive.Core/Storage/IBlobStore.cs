namespace DataspaceDrive.Core.Storage;

/// <summary>Größe und Prüfsumme eines gespeicherten Blobs.</summary>
public sealed record BlobInfo(string Key, long Length, string Sha256);

/// <summary>
/// Ablage für Dateiinhalte. Blobs sind unveränderlich: eine neue Dateiversion bekommt einen neuen Schlüssel.
/// </summary>
public interface IBlobStore
{
    /// <summary>Schreibt den Stream vollständig und berechnet dabei Größe und SHA-256.</summary>
    Task<BlobInfo> WriteAsync(string key, Stream content, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

public static class BlobKey
{
    /// <summary>Erlaubt sind relative Pfade aus Kleinbuchstaben, Ziffern, '-', '_' und '/' ohne leere Segmente.</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 512 || key[0] == '/' || key[^1] == '/')
            return false;
        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
            foreach (var c in segment)
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                    return false;
        }
        return true;
    }

    public static void EnsureValid(string key)
    {
        if (!IsValid(key))
            throw new ArgumentException($"Ungültiger Blob-Schlüssel: '{key}'.", nameof(key));
    }
}
