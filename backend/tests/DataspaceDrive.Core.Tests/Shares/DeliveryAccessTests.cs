using DataspaceDrive.Core.Shares;

namespace DataspaceDrive.Core.Tests.Shares;

public class DeliveryAccessTests
{
    private const string Recipient = "BPNL00000000WB02";
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static DeliverySnapshot Delivery(DeliveryState state = DeliveryState.Active, bool blocked = false) =>
        new("d-1", state, Now.AddDays(1), Recipient, blocked, "drive-d-d-1");

    private static readonly AgreementSnapshot Agreement = new("agr-1", "drive-d-d-1", Retired: false);

    [Fact]
    public void Allows_the_recipient_with_a_matching_agreement() =>
        Assert.Equal(DeliveryAccessDecision.Allowed, DeliveryAccess.Evaluate(true, Delivery(), Recipient, Agreement, Now));

    [Fact]
    public void Invalid_token_wins_over_everything_else() =>
        Assert.Equal(DeliveryAccessDecision.InvalidToken, DeliveryAccess.Evaluate(false, Delivery(), Recipient, Agreement, Now));

    [Fact]
    public void Unknown_delivery_is_reported() =>
        Assert.Equal(DeliveryAccessDecision.UnknownDelivery, DeliveryAccess.Evaluate(true, null, Recipient, Agreement, Now));

    [Theory]
    [InlineData(DeliveryState.Provisioning)]
    [InlineData(DeliveryState.Revoking)]
    [InlineData(DeliveryState.Revoked)]
    [InlineData(DeliveryState.Expired)]
    public void Only_active_deliveries_are_served(DeliveryState state) =>
        Assert.Equal(DeliveryAccessDecision.NotActive, DeliveryAccess.Evaluate(true, Delivery(state), Recipient, Agreement, Now));

    [Fact]
    public void Expiry_takes_effect_before_the_expiry_job_runs() =>
        Assert.Equal(DeliveryAccessDecision.Expired, DeliveryAccess.Evaluate(true, Delivery(), Recipient, Agreement, Now.AddDays(2)));

    [Fact]
    public void Blocked_partner_is_refused_immediately() =>
        Assert.Equal(DeliveryAccessDecision.PartnerBlocked,
            DeliveryAccess.Evaluate(true, Delivery(blocked: true), Recipient, Agreement, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Missing_consumer_identity_is_refused(string? bpn) =>
        Assert.Equal(DeliveryAccessDecision.MissingConsumerIdentity, DeliveryAccess.Evaluate(true, Delivery(), bpn, Agreement, Now));

    [Fact]
    public void Other_company_is_refused_even_with_a_valid_token() =>
        Assert.Equal(DeliveryAccessDecision.WrongConsumer,
            DeliveryAccess.Evaluate(true, Delivery(), "BPNL00000000WD04", Agreement, Now));

    [Fact]
    public void Agreement_for_another_asset_is_refused() =>
        Assert.Equal(DeliveryAccessDecision.AgreementMismatch,
            DeliveryAccess.Evaluate(true, Delivery(), Recipient, Agreement with { AssetId = "drive-d-other" }, Now));

    [Fact]
    public void Retired_agreement_is_refused() =>
        Assert.Equal(DeliveryAccessDecision.AgreementMismatch,
            DeliveryAccess.Evaluate(true, Delivery(), Recipient, Agreement with { Retired = true }, Now));

    [Fact]
    public void Missing_agreement_is_refused() =>
        Assert.Equal(DeliveryAccessDecision.AgreementMismatch, DeliveryAccess.Evaluate(true, Delivery(), Recipient, null, Now));
}
