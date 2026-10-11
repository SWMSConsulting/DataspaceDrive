namespace DataspaceDrive.Core.Storage;

/// <summary>
/// Zwischenablage für Uploads in Teilstücken. Ein Teilstück wird immer an einer festen Position geschrieben;
/// ein wiederholter Versuch nach einem abgebrochenen Schreibvorgang überschreibt den unvollständigen Rest.
/// </summary>
public interface IUploadStaging
{
    /// <summary>Schreibt das Teilstück ab <paramref name="offset"/> und gibt die Zahl der geschriebenen Bytes zurück.</summary>
    Task<long> WriteChunkAsync(Guid uploadId, long offset, Stream chunk, long maxBytes, CancellationToken ct);

    /// <summary>Übernimmt den vollständigen Upload als unveränderlichen Blob und berechnet dabei die Prüfsumme.</summary>
    Task<BlobInfo> PromoteAsync(Guid uploadId, string blobKey, CancellationToken ct);

    Task DiscardAsync(Guid uploadId, CancellationToken ct);
}
