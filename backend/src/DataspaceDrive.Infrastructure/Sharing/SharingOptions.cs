namespace DataspaceDrive.Infrastructure.Sharing;

/// <summary>Kennung dieses Unternehmens im Dataspace (<c>Drive:Participant</c>).</summary>
public sealed class ParticipantOptions
{
    /// <summary>DID des eigenen Connectors.</summary>
    public string Id { get; set; } = "";

    public string Bpn { get; set; } = "";
    public string DspAddress { get; set; } = "";

    /// <summary>Name des Unternehmens; Partner sehen ihn als Absender im Katalog.</summary>
    public string? Organization { get; set; }
}

/// <summary>Zugang zur Management-API des eigenen Connectors (<c>Drive:Edc</c>).</summary>
public sealed class EdcOptions
{
    public string ManagementUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string VersionProfile { get; set; } = "tractusx-0.12";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ManagementUrl);
}

/// <summary>Freigaben an Partner (<c>Drive:Sharing</c>).</summary>
public sealed class SharingOptions
{
    /// <summary>Kennung dieser Instanz in den privaten Asset-Properties.</summary>
    public string InstanceId { get; set; } = "dataspace-drive";

    /// <summary>Clusterinterne Basis-URL des Content-Endpunkts, die die eigene Dataplane aufruft.</summary>
    public string ContentBaseUrl { get; set; } = "";

    public TimeSpan DefaultLifetime { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromDays(90);
    /// <summary>Höchstzahl Dateien je Freigabe, auch nach dem Auflösen geteilter Ordner.</summary>
    public int MaxItems { get; set; } = 200;

    /// <summary>Vier-Augen-Prinzip, solange die Verwaltung es nicht selbst eingestellt hat.</summary>
    public bool RequireApproval { get; set; }
}

/// <summary>Eingang (<c>Drive:Inbox</c>).</summary>
public sealed class InboxOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Größe der Abschnitte beim Abruf; ein Widerruf wirkt ab dem nächsten Abschnitt.</summary>
    public long SegmentSize { get; set; } = 64L * 1024 * 1024;
}
