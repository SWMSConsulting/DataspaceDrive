using DataspaceDrive.Core.Common;

namespace DataspaceDrive.Core.Inbox;

/// <summary>Zustand eines Angebots, das ein Partner an dieses Unternehmen zugestellt hat (Eingang).</summary>
public enum IncomingOfferState
{
    New,
    Accepted,
    Negotiating,
    AwaitingTransfer,
    Downloading,
    Verifying,
    Scanning,
    Completed,
    Declined,
    Withdrawn,
    Expired,
    Failed,
    Quarantined,
}

public static class IncomingOfferLifecycle
{
    public static readonly Transitions<IncomingOfferState> Transitions = new Transitions<IncomingOfferState>()
        .Allow(IncomingOfferState.New, IncomingOfferState.Accepted, IncomingOfferState.Declined,
            IncomingOfferState.Withdrawn, IncomingOfferState.Expired)
        // Taucht ein zurückgezogenes Angebot wieder im Katalog auf, ist es wieder neu.
        .Allow(IncomingOfferState.Withdrawn, IncomingOfferState.New)
        .Allow(IncomingOfferState.Accepted, IncomingOfferState.Negotiating, IncomingOfferState.Failed)
        .Allow(IncomingOfferState.Negotiating, IncomingOfferState.AwaitingTransfer, IncomingOfferState.Failed,
            IncomingOfferState.Withdrawn)
        .Allow(IncomingOfferState.AwaitingTransfer, IncomingOfferState.Downloading, IncomingOfferState.Failed)
        .Allow(IncomingOfferState.Downloading, IncomingOfferState.Verifying, IncomingOfferState.Failed)
        .Allow(IncomingOfferState.Verifying, IncomingOfferState.Scanning, IncomingOfferState.Failed)
        .Allow(IncomingOfferState.Scanning, IncomingOfferState.Completed, IncomingOfferState.Quarantined,
            IncomingOfferState.Failed)
        .Allow(IncomingOfferState.Failed, IncomingOfferState.Accepted);
}
