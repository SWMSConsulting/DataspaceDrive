namespace DataspaceDrive.Core.Shares;

/// <summary>Was der Content-Endpunkt über eine Zustellung wissen muss.</summary>
public sealed record DeliverySnapshot(
    string DeliveryId,
    DeliveryState State,
    DateTimeOffset ExpiresAt,
    string RecipientBpn,
    bool PartnerBlocked,
    string EdcAssetId);

/// <summary>Vertrag aus dem eigenen EDC, auf den sich ein Abruf beruft.</summary>
public sealed record AgreementSnapshot(string AgreementId, string AssetId, bool Retired);

public enum DeliveryAccessDecision
{
    Allowed,
    InvalidToken,
    UnknownDelivery,
    NotActive,
    Expired,
    PartnerBlocked,
    MissingConsumerIdentity,
    WrongConsumer,
    AgreementMismatch,
}

/// <summary>
/// Prüfkette des Content-Endpunkts. Die Dataplane setzt bei Assets vom Typ HttpData die Header
/// <c>Edc-Bpn</c> und <c>Edc-Contract-Agreement-Id</c>; vom Consumer gesendete Header reicht sie nicht durch.
/// </summary>
public static class DeliveryAccess
{
    public static DeliveryAccessDecision Evaluate(
        bool tokenValid,
        DeliverySnapshot? delivery,
        string? consumerBpn,
        AgreementSnapshot? agreement,
        DateTimeOffset now)
    {
        if (!tokenValid)
            return DeliveryAccessDecision.InvalidToken;
        if (delivery is null)
            return DeliveryAccessDecision.UnknownDelivery;
        if (delivery.State != DeliveryState.Active)
            return DeliveryAccessDecision.NotActive;
        if (now >= delivery.ExpiresAt)
            return DeliveryAccessDecision.Expired;
        if (delivery.PartnerBlocked)
            return DeliveryAccessDecision.PartnerBlocked;
        if (string.IsNullOrWhiteSpace(consumerBpn))
            return DeliveryAccessDecision.MissingConsumerIdentity;
        if (!string.Equals(consumerBpn, delivery.RecipientBpn, StringComparison.Ordinal))
            return DeliveryAccessDecision.WrongConsumer;
        if (agreement is null || agreement.Retired || !string.Equals(agreement.AssetId, delivery.EdcAssetId, StringComparison.Ordinal))
            return DeliveryAccessDecision.AgreementMismatch;
        return DeliveryAccessDecision.Allowed;
    }
}
