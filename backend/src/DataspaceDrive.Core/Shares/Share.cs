namespace DataspaceDrive.Core.Shares;

/// <summary>Freigabe von Dateien an ein oder mehrere Partnerunternehmen; je Partner entsteht eine <see cref="Delivery"/>.</summary>
public sealed class Share
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public string? Message { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Datei einer Freigabe als Schnappschuss der Version zum Zeitpunkt des Teilens.</summary>
public sealed class ShareItem
{
    public Guid Id { get; set; }
    public Guid ShareId { get; set; }

    /// <summary>Kurze, stabile ID im Manifest und im Pfad <c>/items/{itemKey}</c>.</summary>
    public required string ItemKey { get; set; }

    public Guid FileEntryId { get; set; }

    /// <summary>Null, wenn die Version inzwischen endgültig gelöscht wurde; Name, Größe und Prüfsumme bleiben erhalten.</summary>
    public Guid? FileVersionId { get; set; }

    public required string Name { get; set; }
    public long SizeBytes { get; set; }
    public required string Sha256 { get; set; }
    public required string ContentType { get; set; }
    public required string BlobKey { get; set; }

    /// <summary>Relativer Ordnerpfad bei Ordnerfreigaben, z. B. „Begehung 2026/Fotos“; null bei einzeln geteilten Dateien.</summary>
    public string? RelativePath { get; set; }
}

/// <summary>Zustellung an genau ein Partnerunternehmen, im EDC als Asset <c>drive-d-{id:N}</c> mit eigener Contract Definition.</summary>
public sealed class Delivery
{
    public Guid Id { get; set; }
    public Guid ShareId { get; set; }
    public Guid PartnerId { get; set; }
    public required string RecipientBpn { get; set; }
    public DeliveryState State { get; set; }

    /// <summary>Version des Zustell-Schlüssels, mit dem das Token im Asset erzeugt wurde.</summary>
    public int TokenVersion { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public string? LastError { get; set; }
    public int RetrievalCount { get; set; }
    public DateTime? LastRetrievalAt { get; set; }

    /// <summary>Optimistische Sperre gegen gleichzeitigen Widerruf und Aktivierung.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();

    /// <summary>Zustell-ID in EDC-Kennungen und im Content-Pfad.</summary>
    public string ExternalId => Id.ToString("N");
}

/// <summary>Abruf (oder abgelehnter Abrufversuch) einer Zustellung über den Content-Endpunkt.</summary>
public sealed class DeliveryRetrieval
{
    public long Id { get; set; }
    public Guid DeliveryId { get; set; }
    public DateTime OccurredAt { get; set; }
    public required string Decision { get; set; }
    public string? ConsumerBpn { get; set; }
    public string? AgreementId { get; set; }
    public string? ItemKey { get; set; }
    public long? Offset { get; set; }
    public long? Length { get; set; }
}

public enum DeliveryTaskKind
{
    /// <summary>Policies, Asset und Contract Definition im eigenen EDC anlegen.</summary>
    Provision,

    /// <summary>Contract Definition löschen und Agreements zurückziehen (Widerruf oder Ablauf).</summary>
    End,
}

/// <summary>Ausstehende EDC-Operation (Outbox); entsteht in derselben Transaktion wie die fachliche Änderung.</summary>
public sealed class DeliveryTask
{
    public long Id { get; set; }
    public DeliveryTaskKind Kind { get; set; }
    public Guid DeliveryId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedAt { get; set; }
}
