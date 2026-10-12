namespace DataspaceDrive.Core.Inbox;

/// <summary>Zustellung eines Partners an dieses Unternehmen, gefunden im Katalog des Partners.</summary>
public sealed class IncomingOffer
{
    public Guid Id { get; set; }
    public Guid PartnerId { get; set; }
    public required string ProviderBpn { get; set; }
    public required string AssetId { get; set; }
    public required string OfferId { get; set; }
    public required string Title { get; set; }
    public string? Message { get; set; }
    public string? SenderOrganization { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public long TotalSize { get; set; }

    /// <summary>Linie der Freigabe beim Absender; Revisionen derselben Linie aktualisieren bereits übernommene Dateien.</summary>
    public string? LineageId { get; set; }

    public int Revision { get; set; } = 1;

    /// <summary>Vertragsprofil des Absenders; bestimmt die Berechtigungen beim Verhandeln.</summary>
    public string ContractProfile { get; set; } = "membership";

    public IncomingOfferState State { get; set; }

    /// <summary>Beginn des aktuellen Zustands; Wartezeiten der Übernahme zählen ab hier.</summary>
    public DateTime StateChangedAt { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public Guid? TargetFolderId { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }

    public string? NegotiationId { get; set; }
    public string? AgreementId { get; set; }
    public string? TransferProcessId { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? Error { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}

/// <summary>Datei eines Angebots laut Manifest, mit Fortschritt des Abrufs und der Ablage.</summary>
public sealed class IncomingItem
{
    public Guid Id { get; set; }
    public Guid OfferId { get; set; }
    public required string ItemKey { get; set; }
    public required string Name { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public required string ContentType { get; set; }

    /// <summary>Relativer Ordnerpfad aus dem Manifest (bereinigt); die Ablage legt die Ordner unter dem Zielordner an.</summary>
    public string? RelativePath { get; set; }

    /// <summary>Stabile Kennung der Quelldatei beim Absender (über Revisionen gleich).</summary>
    public string? SourceKey { get; set; }

    /// <summary>Bereits empfangene Bytes; ein unterbrochener Abruf setzt hier fort.</summary>
    public long ReceivedBytes { get; set; }

    public Guid? FileEntryId { get; set; }
    public Guid? FileVersionId { get; set; }
}
