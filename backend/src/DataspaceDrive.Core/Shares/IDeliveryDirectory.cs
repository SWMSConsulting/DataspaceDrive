namespace DataspaceDrive.Core.Shares;

/// <summary>Datei einer Zustellung, wie sie der Content-Endpunkt ausliefert.</summary>
public sealed record DeliveryItem(string ItemId, string BlobKey, string FileName, string ContentType, long Length);

/// <summary>Nachschlagen von Zustellungen, Verträgen und Dateien für den Content-Endpunkt.</summary>
public interface IDeliveryDirectory
{
    Task<DeliverySnapshot?> FindDeliveryAsync(string deliveryId, CancellationToken ct);

    Task<AgreementSnapshot?> FindAgreementAsync(string agreementId, CancellationToken ct);

    /// <summary>Ohne <paramref name="itemId"/> die einzige Datei der Zustellung, sonst die genannte.</summary>
    Task<DeliveryItem?> FindItemAsync(string deliveryId, string? itemId, CancellationToken ct);
}
