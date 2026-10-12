using System.Text.Json.Nodes;

namespace Swms.Edc.Client.Policies;

/// <summary>
/// Policies des Drive-Austauschprofils v1. Anbieter und Empfänger kennen dieselben Formen: der Anbieter legt
/// die Contract-Policy damit an, der Empfänger schickt sie beim Verhandeln in genau dieser Form zurück.
/// </summary>
/// <remarks>
/// Der Empfänger-EDC vergleicht die Agreement-Policy per Jackson-Baum mit der Policy aus dem Request
/// (EDC <c>PolicyEquality</c>, ignoriert nur <c>@type</c>, <c>assignee</c> und <c>target</c>). Das kompaktierte
/// Offer aus einem DSP-2025-1-Katalog lässt sich nicht verlustfrei zurückschicken; im Spike scheiterte jede
/// solche Variante mit "Policy in the contract agreement is not equal to the one in the contract offer",
/// während die Profilform für alle getesteten Contract-Policies zu FINALIZED führte.
/// </remarks>
public static class ExchangeProfile
{
    private const string Cx = EdcNamespaces.CxPolicy;

    /// <summary>Access-Policy für genau ein Partnerunternehmen: Mitglied im Dataspace und passende BPNL.</summary>
    public static JsonObject AccessPolicy(string policyId, IEnumerable<string> bpns) =>
        PolicyDefinition(policyId, new JsonArray(new JsonObject
        {
            ["action"] = Cx + "access",
            ["constraint"] = new JsonObject
            {
                ["@type"] = "LogicalConstraint",
                ["and"] = new JsonArray(
                    Constraint(Cx + "Membership", "eq", "active"),
                    Constraint(Cx + "BusinessPartnerNumber", "isAnyOf", new JsonArray(bpns.Select(b => (JsonNode)b!).ToArray()))),
            },
        }));

    /// <summary>
    /// Berechtigungen der Contract-Policy (Profil "open"): keine. Den Empfänger legt allein die Access-Policy fest, die
    /// der Anbieter beim Katalog und bei der Verhandlung prüft. Eine leere Policy übersteht den Umweg über den Katalog
    /// unverändert; deshalb können auch allgemeine Werkzeuge wie EdcAdmin solche Zustellungen verhandeln.
    /// </summary>
    public static JsonArray OpenContractPermissions() => [];

    /// <summary>
    /// Berechtigungen der Contract-Policy (Profil "membership"). Läuft durch die Policy-Validierung, weil die
    /// Action <c>access</c> ist; die BPN-Einschränkung steckt in der Access-Policy, die der Anbieter bei der
    /// Verhandlung erneut prüft.
    /// </summary>
    public static JsonArray MembershipContractPermissions() =>
        new(new JsonObject
        {
            ["action"] = Cx + "access",
            ["constraint"] = Constraint(Cx + "Membership", "eq", "active"),
        });

    /// <summary>
    /// Catena-X-konforme Usage-Berechtigungen (Profil "cx"). Nur verhandelbar, wenn das
    /// DataExchangeGovernanceCredential des Empfängers <c>contractVersion "1.0"</c> trägt; der Connector
    /// vergleicht exakt (<c>FrameworkAgreementConstraintFunction</c>).
    /// </summary>
    public static JsonArray CxUsagePermissions(params string[] usagePurposes) =>
        new(new JsonObject
        {
            ["action"] = EdcNamespaces.Odrl + "use",
            ["constraint"] = new JsonObject
            {
                ["@type"] = "LogicalConstraint",
                ["and"] = new JsonArray(
                    Constraint(Cx + "FrameworkAgreement", "eq", "DataExchangeGovernance:1.0"),
                    Constraint(Cx + "UsagePurpose", "isAnyOf", new JsonArray(usagePurposes.Select(p => (JsonNode)p!).ToArray()))),
            },
        });

    public static JsonObject PolicyDefinition(string policyId, JsonArray permissions) => new()
    {
        ["@context"] = ProfileContext(),
        ["@type"] = "PolicyDefinition",
        ["@id"] = policyId,
        ["policy"] = new JsonObject { ["@type"] = "Set", ["permission"] = permissions },
    };

    /// <summary>Kontext, mit dem Anbieter und Empfänger die Profil-Policies schreiben.</summary>
    public static JsonArray ProfileContext() =>
        new(EdcNamespaces.DspaceOdrlProfileContext, new JsonObject { ["@vocab"] = EdcNamespaces.Edc, ["odrl"] = EdcNamespaces.Odrl });

    private static JsonObject Constraint(string leftOperand, string @operator, JsonNode rightOperand) => new()
    {
        ["leftOperand"] = leftOperand,
        ["operator"] = @operator,
        ["rightOperand"] = rightOperand,
    };
}
