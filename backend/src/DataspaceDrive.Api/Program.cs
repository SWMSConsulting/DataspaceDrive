using System.Text.Json.Serialization;
using DataspaceDrive.Api;
using DataspaceDrive.Api.Auth;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Infrastructure;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

// DataspaceDrive API/BFF:
//  - Port "App" (8080): Angular-SPA und REST-API für die Mitarbeitenden, über den Ingress erreichbar.
//  - Port "Content" (8090): nur /edc/*, der Content-Endpunkt für die EDC-Dataplane des eigenen Connectors.
//    Im Cluster schützt zusätzlich eine NetworkPolicy, die nur die Dataplane-Pods zulässt.
// Die Trennung prüft den tatsächlichen Socket-Port (Connection.LocalPort), nicht den fälschbaren Host-Header.

var builder = WebApplication.CreateBuilder(args);

var ports = builder.Configuration.GetSection("Drive:Ports").Get<DrivePorts>() ?? new DrivePorts();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(ports.App);
    kestrel.ListenAnyIP(ports.Content);
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DriveExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(ports);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => DeliveryTokenConfiguration.Create(
    builder.Configuration.GetSection("Drive:DeliveryTokens"), builder.Environment, sp.GetRequiredService<ILogger<Program>>()));
builder.Services.AddScoped<ContentServices>();
builder.Services.AddDriveInfrastructure(builder.Configuration);
builder.Services.AddDriveAuthentication();
builder.Services.AddDriveAntiforgery();
builder.Services.AddScoped<CurrentUser>();

var app = builder.Build();

// Prüfungen nach dem Aufbau, damit alle Konfigurationsquellen (auch Test-Overrides) berücksichtigt sind.
AuthSetup.ValidateAuthConfiguration(app.Configuration);
// Ohne Virenscanner würden Uploads ungeprüft freigegeben; das ist nur in der Entwicklung zulässig.
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(app.Configuration["Scanning:ClamAv:Host"])
    && app.Configuration.GetValue("Scanning:Required", true))
    throw new InvalidOperationException("Scanning:ClamAv:Host ist nicht gesetzt. Ohne Virenscan nur mit Scanning:Required=false starten.");

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
    if (db.Database.IsNpgsql())
        await WaitForDatabaseAsync(db.Database.GetConnectionString()!, app.Logger);
    await db.Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UsePortSeparation(ports);
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseDriveAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/config", (IConfiguration cfg, IUploadService uploads) => Results.Ok(new
{
    participantId = cfg["Drive:Participant:Id"],
    bpn = cfg["Drive:Participant:Bpn"],
    dspAddress = cfg["Drive:Participant:DspAddress"],
    edcProfile = cfg["Drive:Edc:VersionProfile"],
    maxUploadBytes = uploads.MaxFileSize,
})).RequireAuthorization(AuthSetup.UserPolicy);

app.MapAuthEndpoints(app.Configuration);
app.MapFileEndpoints();
app.MapAdminEndpoints();
app.MapSharingEndpoints();
app.MapContentEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

// Die Postgres im selben Release startet unter Umständen später als die Anwendung; kurz warten statt abzustürzen.
// Direkt über Npgsql, damit EF Core nicht jeden Fehlversuch als Fehler protokolliert.
static async Task WaitForDatabaseAsync(string connectionString, ILogger logger)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return;
        }
        catch (Exception ex) when (attempt < 30 && ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            logger.LogWarning("Datenbank noch nicht erreichbar (Versuch {Attempt}/30): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

public partial class Program;
