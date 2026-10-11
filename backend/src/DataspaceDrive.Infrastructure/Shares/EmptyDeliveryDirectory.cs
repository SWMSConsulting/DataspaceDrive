using DataspaceDrive.Core.Shares;

namespace DataspaceDrive.Infrastructure.Shares;

/// <summary>Platzhalter bis zur Persistenz der Freigaben (Phase 2): kennt keine Zustellungen.</summary>
public sealed class EmptyDeliveryDirectory : IDeliveryDirectory
{
    public Task<DeliverySnapshot?> FindDeliveryAsync(string deliveryId, CancellationToken ct) =>
        Task.FromResult<DeliverySnapshot?>(null);

    public Task<AgreementSnapshot?> FindAgreementAsync(string agreementId, CancellationToken ct) =>
        Task.FromResult<AgreementSnapshot?>(null);

    public Task<DeliveryItem?> FindItemAsync(string deliveryId, string? itemId, CancellationToken ct) =>
        Task.FromResult<DeliveryItem?>(null);
}
