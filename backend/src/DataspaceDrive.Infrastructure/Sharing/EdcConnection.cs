using Microsoft.Extensions.Options;
using Swms.Edc.Client;

namespace DataspaceDrive.Infrastructure.Sharing;

/// <summary>
/// Zugang zum eigenen Connector. Die Konfiguration wird erst beim Zugriff gelesen, damit auch Overrides nach dem
/// Programmstart (Tests) greifen; ohne Management-URL laufen Freigaben und Eingang nicht.
/// </summary>
public sealed class EdcConnection(IHttpClientFactory httpClients, IOptions<EdcOptions> options)
{
    public const string HttpClientName = "edc-management";

    public bool IsConfigured => options.Value.IsConfigured;

    public EdcManagementClient Client => IsConfigured
        ? new EdcManagementClient(httpClients.CreateClient(HttpClientName), EdcVersionProfile.FromName(options.Value.VersionProfile))
        : throw new InvalidOperationException("Kein EDC konfiguriert (Drive:Edc:ManagementUrl).");

    public EdcManagementClient? TryClient => IsConfigured ? Client : null;
}

/// <summary>Hintergrunddienste für Freigaben und Eingang (<c>Drive:BackgroundWorkers</c>); Tests schalten sie ab.</summary>
public sealed class BackgroundWorkerOptions
{
    public bool Enabled { get; set; } = true;
}
