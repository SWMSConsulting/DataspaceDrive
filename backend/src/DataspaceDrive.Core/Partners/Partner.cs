namespace DataspaceDrive.Core.Partners;

/// <summary>
/// Partnerunternehmen im Dataspace, gepflegt von Administratoren. Die DID löst der eigene Connector über das BDRS
/// auf; die DSP-Adresse muss die Verwaltung kennen, weil die DID-Dokumente keinen DataService-Eintrag tragen.
/// </summary>
public sealed class Partner
{
    public Guid Id { get; set; }
    public required string Bpn { get; set; }
    public required string Name { get; set; }

    /// <summary>DSP-Basisadresse des Partner-Connectors, z. B. <c>https://bob-edc/api/v1/dsp</c>.</summary>
    public required string DspAddress { get; set; }

    /// <summary>Kennung aus der Discovery, unter DSP 2025-1 die DID.</summary>
    public string? Did { get; set; }

    public string? Protocol { get; set; }

    /// <summary>Versionsspezifische Adresse aus der Discovery (…/dsp/2025-1).</summary>
    public string? ProtocolAddress { get; set; }

    public DateTime? ResolvedAt { get; set; }
    public string? ResolveError { get; set; }

    /// <summary>Nutzer dürfen an diesen Partner freigeben.</summary>
    public bool SharingEnabled { get; set; } = true;

    /// <summary>Der Eingang fragt den Katalog dieses Partners ab.</summary>
    public bool ReceivingEnabled { get; set; } = true;

    /// <summary>Gesperrt: keine neuen Freigaben, keine Auslieferung, kein Empfang.</summary>
    public bool Blocked { get; set; }

    /// <summary>Angebote dieses Partners ohne Rückfrage übernehmen (Standard: aus).</summary>
    public AutoAcceptMode AutoAccept { get; set; } = AutoAcceptMode.Off;

    /// <summary>Zielordner bei <see cref="AutoAcceptMode.All"/>.</summary>
    public Guid? AutoAcceptFolderId { get; set; }

    /// <summary>In dessen Namen automatisch übernommene Dateien abgelegt werden (wer die Einstellung vorgenommen hat).</summary>
    public Guid? AutoAcceptUserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public string? LastPollError { get; set; }

    public bool IsResolved => Did is not null && Protocol is not null && ProtocolAddress is not null;
}

public enum AutoAcceptMode
{
    Off,

    /// <summary>Neue Revisionen bereits übernommener Freigaben an den bisherigen Ort.</summary>
    UpdatesOnly,

    /// <summary>Jedes Angebot; neue in den festen Zielordner, Revisionen an den bisherigen Ort.</summary>
    All,
}
