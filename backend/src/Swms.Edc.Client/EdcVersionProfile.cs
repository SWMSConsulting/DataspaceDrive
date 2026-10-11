namespace Swms.Edc.Client;

/// <summary>
/// Pfade und Verhalten, die sich zwischen Tractus-X-EDC-Versionen unterscheiden. Das Profil wird per
/// Konfiguration gewählt, damit ein Connector-Upgrade keine Codeänderung in der Drive erzwingt.
/// </summary>
public sealed record EdcVersionProfile(
    string Name,
    string DspVersionDiscoveryPath,
    bool IdentityHeadersForAllAddressTypes)
{
    /// <summary>tractusx-edc 0.12.x (EDC 0.15.x), im windx-Cluster verifiziert (Spike 2026-10-11).</summary>
    public static readonly EdcVersionProfile Tractusx012 = new(
        "tractusx-0.12",
        "v4alpha/connectordiscovery/dspversionparams",
        IdentityHeadersForAllAddressTypes: false);

    /// <summary>tractusx-edc 0.13.x laut Migrationsleitfaden; vor dem Einsatz gegen ein 0.13-Release prüfen.</summary>
    public static readonly EdcVersionProfile Tractusx013 = new(
        "tractusx-0.13",
        "v3/connectordiscovery/dspversionparams",
        IdentityHeadersForAllAddressTypes: true);

    public static EdcVersionProfile FromName(string name) => name switch
    {
        "tractusx-0.12" => Tractusx012,
        "tractusx-0.13" => Tractusx013,
        _ => throw new ArgumentException($"Unbekanntes EDC-Versionsprofil '{name}'.", nameof(name)),
    };
}
