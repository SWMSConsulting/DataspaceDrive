using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Scanning;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Scanning;
using DataspaceDrive.Infrastructure.Shares;
using DataspaceDrive.Infrastructure.Storage;
using DataspaceDrive.Infrastructure.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddScoped<IUserProvisioning, UserProvisioning>();
        services.AddSingleton<IDeliveryDirectory, EmptyDeliveryDirectory>();

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
