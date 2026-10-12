using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Partners;
using DataspaceDrive.Core.Settings;
using DataspaceDrive.Core.Scanning;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Scanning;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Infrastructure.Audit;
using DataspaceDrive.Infrastructure.Inbox;
using DataspaceDrive.Infrastructure.Partners;
using DataspaceDrive.Infrastructure.Sharing;
using DataspaceDrive.Infrastructure.Storage;
using DataspaceDrive.Infrastructure.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DataspaceDrive.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDriveInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<DriveDbContext>(options => options.UseNpgsql(PostgresConnectionString(configuration)));

        services.AddOptions<FileSystemBlobStoreOptions>().Bind(configuration.GetSection("Storage:FileSystem"));
        services.AddSingleton<FileSystemBlobStore>();
        services.AddSingleton<IBlobStore>(sp => sp.GetRequiredService<FileSystemBlobStore>());
        services.AddSingleton<IUploadStaging, FileSystemUploadStaging>();

        services.AddOptions<UploadOptions>().Bind(configuration.GetSection("Uploads"));
        services.AddScoped<AccessResolver>();
        services.AddScoped<IFileService, FileService>();
        services.AddScoped<IUploadService, UploadService>();
        services.AddScoped<ITeamSpaceService, TeamSpaceService>();
        services.AddHostedService<UploadCleanupWorker>();
        services.AddScoped<IUserProvisioning, UserProvisioning>();
        services.AddScoped<IDeliveryDirectory, DeliveryDirectory>();
        services.AddMemoryCache();

        // Freigaben und Eingang über den eigenen Connector
        services.AddOptions<ParticipantOptions>().Bind(configuration.GetSection("Drive:Participant"));
        services.AddOptions<EdcOptions>().Bind(configuration.GetSection("Drive:Edc"));
        services.AddOptions<SharingOptions>().Bind(configuration.GetSection("Drive:Sharing"));
        services.AddOptions<InboxOptions>().Bind(configuration.GetSection("Drive:Inbox"));
        services.AddOptions<BackgroundWorkerOptions>().Bind(configuration.GetSection("Drive:BackgroundWorkers"));
        services.AddHttpClient(EdcConnection.HttpClientName, (sp, http) =>
        {
            var edc = sp.GetRequiredService<IOptions<EdcOptions>>().Value;
            if (edc.IsConfigured)
                http.BaseAddress = new Uri(edc.ManagementUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("X-Api-Key", edc.ApiKey);
            http.Timeout = TimeSpan.FromMinutes(2);
        });
        // Abschnitte großer Dateien über die Dataplane des Anbieters; das Zeitlimit gilt je Abschnitt.
        services.AddHttpClient(IncomingTransferWorker.DataplaneClient, http => http.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<EdcConnection>();
        services.AddScoped<IPartnerService, PartnerService>();
        services.AddScoped<IShareService, ShareService>();
        services.AddScoped<IInstanceSettings, InstanceSettingsService>();
        services.AddScoped<IInboxService, InboxService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddSingleton<InboxPoller>();
        services.AddSingleton<IInboxRefresh>(sp => sp.GetRequiredService<InboxPoller>());
        services.AddHostedService<DeliveryWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<InboxPoller>());
        services.AddHostedService<IncomingTransferWorker>();
        services.AddSingleton<EdcReconciliation>();
        services.AddHostedService(sp => sp.GetRequiredService<EdcReconciliation>());

        services.AddOptions<ClamAvOptions>().Bind(configuration.GetSection("Scanning:ClamAv"));
        if (!string.IsNullOrWhiteSpace(configuration["Scanning:ClamAv:Host"]))
        {
            services.AddSingleton<IVirusScanner, ClamAvScanner>();
            services.AddHostedService<ScanWorker>();
        }
        else
        {
            services.AddSingleton<IVirusScanner, DisabledVirusScanner>();
        }

        // Schlüssel für Cookies und Antiforgery in der Datenbank, damit Sitzungen einen Neustart überstehen.
        services.AddDataProtection()
            .SetApplicationName("DataspaceDrive")
            .PersistKeysToDbContext<DriveDbContext>();
        return services;
    }

    // Ohne Kerberos: sonst versucht Npgsql beim Verbindungsaufbau libgssapi zu laden, die im Container fehlt.
    private static string PostgresConnectionString(IConfiguration configuration) =>
        new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Drive")) { GssEncryptionMode = GssEncryptionMode.Disable }
            .ConnectionString;
}
