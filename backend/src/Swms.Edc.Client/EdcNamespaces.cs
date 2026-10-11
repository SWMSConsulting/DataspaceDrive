namespace Swms.Edc.Client;

/// <summary>JSON-LD-Namensräume und Kontexte der Tractus-X-EDC-Management-API.</summary>
public static class EdcNamespaces
{
    public const string Edc = "https://w3id.org/edc/v0.0.1/ns/";
    public const string Odrl = "http://www.w3.org/ns/odrl/2/";
    public const string Tx = "https://w3id.org/tractusx/v0.0.1/ns/";
    public const string Dct = "http://purl.org/dc/terms/";

    /// <summary>Catena-X-Policy-Vokabular 2025/9 (Constraints und die Action <c>access</c>).</summary>
    public const string CxPolicy = "https://w3id.org/catenax/2025/9/policy/";

    /// <summary>ODRL-Profil von DSP 2025-1; damit legt der Anbieter Policies an.</summary>
    public const string DspaceOdrlProfileContext = "https://w3id.org/dspace/2025/1/odrl-profile.jsonld";

    public const string Dsp2025Protocol = "dataspace-protocol-http:2025-1";
}
