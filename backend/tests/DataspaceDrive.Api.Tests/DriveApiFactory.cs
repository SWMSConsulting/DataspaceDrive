using System.Security.Claims;
using System.Text.Encodings.Web;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Scanning;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Api.Tests;

/// <summary>
/// Testhost der API: SQLite im Speicher statt PostgreSQL, Test-Anmeldung über die Header <c>X-Test-User</c> und
/// <c>X-Test-Roles</c>, echter Antiforgery-Schutz über HTTPS. Der TestServer kennt keine echten Ports; der
/// Header <c>X-Test-Local-Port</c> setzt <c>Connection.LocalPort</c> für die Port-Trennung.
/// </summary>
public sealed class DriveApiFactory : WebApplicationFactory<Program>
{
    public const int AppPort = 8080;
    public const int ContentPort = 8090;
    public static readonly byte[] TokenKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "drive-api-tests-" + Guid.NewGuid().ToString("N"));

    public FakeDeliveryDirectory Directory { get; } = new();

    public FakeVirusScanner Scanner { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Drive:Ports:App"] = AppPort.ToString(),
            ["Drive:Ports:Content"] = ContentPort.ToString(),
            ["Drive:DeliveryTokens:Keys:1"] = Convert.ToBase64String(TokenKey),
            ["Storage:FileSystem:RootPath"] = DataRoot,
            ["Uploads:ChunkSize"] = (1024 * 1024).ToString(),
            ["Database:MigrateOnStartup"] = "false",
            ["Scanning:Required"] = "false",
            ["Auth:Mode"] = "Oidc",
            ["Auth:Oidc:Authority"] = "https://idp.invalid/realms/test",
            ["Auth:Oidc:ClientId"] = "dataspace-drive",
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<DriveDbContext>>();
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<DriveDbContext>));
            services.AddDbContext<DriveDbContext>(o => o.UseSqlite(_connection));

            services.RemoveAll<IDeliveryDirectory>();
            services.AddSingleton<IDeliveryDirectory>(Directory);
            services.RemoveAll<IVirusScanner>();
            services.AddSingleton<IVirusScanner>(Scanner);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
            services.AddSingleton<IStartupFilter, TestPortStartupFilter>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DriveDbContext>().Database.EnsureCreated();
        return host;
    }

    public IBlobStore Blobs => Services.GetRequiredService<IBlobStore>();

    /// <summary>Client für einen Testnutzer; holt das XSRF-Token und schickt es bei ändernden Requests mit.</summary>
    public async Task<HttpClient> ClientForAsync(string user, params string[] roles)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Test-User", user);
        client.DefaultRequestHeaders.Add("X-Test-Roles", string.Join(',', roles.Length > 0 ? roles : [DrivePrincipal.RoleUser]));
        client.DefaultRequestHeaders.Add("X-Test-Local-Port", AppPort.ToString());
        var me = await client.GetAsync("/api/me");
        me.EnsureSuccessStatusCode();
        var xsrf = me.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])
            .First(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))["XSRF-TOKEN=".Length..];
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf));
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
        if (System.IO.Directory.Exists(DataRoot))
            System.IO.Directory.Delete(DataRoot, recursive: true);
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

public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (string.IsNullOrEmpty(user))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("sub", user), new("name", user), new("email", $"{user}@example.org") };
        claims.AddRange(Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => new Claim("roles", r)));
        var identity = new ClaimsIdentity(claims, SchemeName, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
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

/// <summary>Scanner für Tests: Inhalt mit „EICAR“ gilt als infiziert, alles andere als sauber.</summary>
public sealed class FakeVirusScanner : IVirusScanner
{
    public bool Enabled { get; set; }

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        using var reader = new StreamReader(content);
        var text = await reader.ReadToEndAsync(ct);
        return text.Contains("EICAR", StringComparison.Ordinal)
            ? new ScanResult(ScanVerdict.Infected, "Eicar-Test-Signature")
            : new ScanResult(ScanVerdict.Clean);
    }
}
