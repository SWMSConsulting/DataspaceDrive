using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Shares;
using DataspaceDrive.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataspaceDrive.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDriveInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FileSystemBlobStoreOptions>().Bind(configuration.GetSection("Storage:FileSystem"));
        services.AddSingleton<IBlobStore, FileSystemBlobStore>();
        services.AddSingleton<IDeliveryDirectory, EmptyDeliveryDirectory>();
        return services;
    }
}
