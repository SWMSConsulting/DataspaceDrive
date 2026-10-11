using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataspaceDrive.Api.Tests;

/// <summary>
/// Testhost der API. Der TestServer kennt keine echten Ports; der Header <c>X-Test-Local-Port</c> setzt
/// deshalb <c>Connection.LocalPort</c>, damit die Port-Trennung realistisch geprüft werden kann.
/// </summary>
public sealed class DriveApiFactory : WebApplicationFactory<Program>
{
    public const int AppPort = 8080;
    public const int ContentPort = 8090;
    public static readonly byte[] TokenKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    public string BlobRoot { get; } = Path.Combine(Path.GetTempPath(), "drive-api-tests-" + Guid.NewGuid().ToString("N"));

    public FakeDeliveryDirectory Directory { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Drive:Ports:App", AppPort.ToString());
        builder.UseSetting("Drive:Ports:Content", ContentPort.ToString());
        builder.UseSetting("Drive:DeliveryTokens:Keys:1", Convert.ToBase64String(TokenKey));
        builder.UseSetting("Storage:FileSystem:RootPath", BlobRoot);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDeliveryDirectory>();
            services.AddSingleton<IDeliveryDirectory>(Directory);
            services.AddSingleton<IStartupFilter, TestPortStartupFilter>();
        });
    }

    public IBlobStore Blobs => Services.GetRequiredService<IBlobStore>();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (System.IO.Directory.Exists(BlobRoot))
            System.IO.Directory.Delete(BlobRoot, recursive: true);
    }

    private sealed class TestPortStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (int.TryParse(context.Request.Headers["X-Test-Local-Port"], out var port))
                    context.Connection.LocalPort = port;
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

public sealed class FakeDeliveryDirectory : IDeliveryDirectory
{
    public Dictionary<string, DeliverySnapshot> Deliveries { get; } = [];
    public Dictionary<string, AgreementSnapshot> Agreements { get; } = [];
    public Dictionary<string, DeliveryItem> Items { get; } = [];

    public Task<DeliverySnapshot?> FindDeliveryAsync(string deliveryId, CancellationToken ct) =>
        Task.FromResult(Deliveries.GetValueOrDefault(deliveryId));

    public Task<AgreementSnapshot?> FindAgreementAsync(string agreementId, CancellationToken ct) =>
        Task.FromResult(Agreements.GetValueOrDefault(agreementId));

    public Task<DeliveryItem?> FindItemAsync(string deliveryId, string? itemId, CancellationToken ct) =>
        Task.FromResult(Items.GetValueOrDefault(deliveryId));
}
