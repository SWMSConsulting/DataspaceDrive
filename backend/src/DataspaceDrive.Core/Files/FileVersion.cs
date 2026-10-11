namespace DataspaceDrive.Core.Files;

public enum ScanStatus
{
    /// <summary>Wartet auf den Virenscan.</summary>
    Pending,
    Clean,
    Infected,

    /// <summary>Zu groß oder nicht prüfbar (z. B. verschlüsseltes Archiv); gesperrt bis zur Freigabe.</summary>
    NotScannable,
    Error,

    /// <summary>Virenscan ist abgeschaltet (nur Entwicklung).</summary>
    NotScanned,
}

/// <summary>Unveränderliche Version einer Datei; ihr Inhalt liegt als Blob unter <see cref="BlobKey"/>.</summary>
public sealed class FileVersion
{
    public Guid Id { get; set; }
    public Guid FileEntryId { get; set; }
    public int VersionNo { get; set; }
    public required string BlobKey { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public required string ContentType { get; set; }
    public ScanStatus ScanStatus { get; set; }
    public string? ScanDetail { get; set; }
    public DateTime? ScannedAt { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }

    /// <summary>Nur geprüfte Inhalte dürfen heruntergeladen und geteilt werden.</summary>
    public bool IsAvailable => ScanStatus is ScanStatus.Clean or ScanStatus.NotScanned;
}
