namespace DataspaceDrive.Core.Scanning;

public enum ScanVerdict
{
    Clean,
    Infected,
    NotScannable,
    Error,
}

public sealed record ScanResult(ScanVerdict Verdict, string? Detail = null);

public interface IVirusScanner
{
    /// <summary>False, wenn kein Scanner konfiguriert ist (nur Entwicklung); Dateien gelten dann als ungeprüft.</summary>
    bool Enabled { get; }

    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct);
}
