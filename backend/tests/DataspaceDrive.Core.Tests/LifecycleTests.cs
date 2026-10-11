using DataspaceDrive.Core.Common;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Shares;

namespace DataspaceDrive.Core.Tests;

public class LifecycleTests
{
    [Theory]
    [InlineData(DeliveryState.Requested, DeliveryState.Provisioning)]
    [InlineData(DeliveryState.Provisioning, DeliveryState.Active)]
    [InlineData(DeliveryState.Provisioning, DeliveryState.ProvisioningFailed)]
    [InlineData(DeliveryState.ProvisioningFailed, DeliveryState.Provisioning)]
    [InlineData(DeliveryState.Active, DeliveryState.Revoking)]
    [InlineData(DeliveryState.Revoking, DeliveryState.Revoked)]
    [InlineData(DeliveryState.Active, DeliveryState.Expiring)]
    [InlineData(DeliveryState.Expiring, DeliveryState.Expired)]
    public void Delivery_allows_documented_transitions(DeliveryState from, DeliveryState to) =>
        Assert.Equal(to, DeliveryLifecycle.Transitions.Move(from, to));

    [Theory]
    [InlineData(DeliveryState.Requested, DeliveryState.Active)]
    [InlineData(DeliveryState.Revoked, DeliveryState.Active)]
    [InlineData(DeliveryState.Expired, DeliveryState.Active)]
    [InlineData(DeliveryState.Active, DeliveryState.Revoked)]
    public void Delivery_rejects_shortcuts(DeliveryState from, DeliveryState to) =>
        Assert.Throws<InvalidStateTransitionException>(() => DeliveryLifecycle.Transitions.Move(from, to));

    [Theory]
    [InlineData(DeliveryState.Rejected, true)]
    [InlineData(DeliveryState.Revoked, true)]
    [InlineData(DeliveryState.Expired, true)]
    [InlineData(DeliveryState.Active, false)]
    [InlineData(DeliveryState.Revoking, false)]
    public void Delivery_final_states(DeliveryState state, bool final) =>
        Assert.Equal(final, DeliveryLifecycle.IsFinal(state));

    [Fact]
    public void Incoming_offer_happy_path()
    {
        var path = new[]
        {
            IncomingOfferState.New, IncomingOfferState.Accepted, IncomingOfferState.Negotiating, IncomingOfferState.AwaitingTransfer,
            IncomingOfferState.Downloading, IncomingOfferState.Verifying, IncomingOfferState.Scanning, IncomingOfferState.Completed,
        };
        for (var i = 1; i < path.Length; i++)
            Assert.True(IncomingOfferLifecycle.Transitions.CanMove(path[i - 1], path[i]), $"{path[i - 1]} -> {path[i]}");
    }

    [Fact]
    public void Withdrawn_offer_can_reappear_and_failed_offer_can_be_retried()
    {
        Assert.True(IncomingOfferLifecycle.Transitions.CanMove(IncomingOfferState.Withdrawn, IncomingOfferState.New));
        Assert.True(IncomingOfferLifecycle.Transitions.CanMove(IncomingOfferState.Failed, IncomingOfferState.Accepted));
    }

    [Fact]
    public void Quarantined_offer_is_final() =>
        Assert.DoesNotContain(Enum.GetValues<IncomingOfferState>(),
            s => IncomingOfferLifecycle.Transitions.CanMove(IncomingOfferState.Quarantined, s));
}
