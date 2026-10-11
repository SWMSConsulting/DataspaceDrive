using DataspaceDrive.Core.Common;

namespace DataspaceDrive.Core.Shares;

/// <summary>
/// Zustand einer Zustellung: eine Freigabe an genau ein Partnerunternehmen, im EDC abgebildet als
/// eigenes Asset mit eigener Contract Definition.
/// </summary>
public enum DeliveryState
{
    Requested,
    Provisioning,
    ProvisioningFailed,
    Rejected,
    Active,
    Revoking,
    Revoked,
    Expiring,
    Expired,
}

public static class DeliveryLifecycle
{
    public static readonly Transitions<DeliveryState> Transitions = new Transitions<DeliveryState>()
        .Allow(DeliveryState.Requested, DeliveryState.Provisioning, DeliveryState.Rejected)
        .Allow(DeliveryState.Provisioning, DeliveryState.Active, DeliveryState.ProvisioningFailed)
        .Allow(DeliveryState.ProvisioningFailed, DeliveryState.Provisioning, DeliveryState.Revoking)
        .Allow(DeliveryState.Active, DeliveryState.Revoking, DeliveryState.Expiring)
        .Allow(DeliveryState.Revoking, DeliveryState.Revoked)
        .Allow(DeliveryState.Expiring, DeliveryState.Expired);

    /// <summary>In Endzuständen legt die Drive keine EDC-Objekte mehr an und liefert keine Inhalte aus.</summary>
    public static bool IsFinal(DeliveryState state) =>
        state is DeliveryState.Rejected or DeliveryState.Revoked or DeliveryState.Expired;
}
