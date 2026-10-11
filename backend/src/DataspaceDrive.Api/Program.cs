using DataspaceDrive.Api;
using DataspaceDrive.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;

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
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(ports);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => DeliveryTokenConfiguration.Create(
    builder.Configuration.GetSection("Drive:DeliveryTokens"), builder.Environment, sp.GetRequiredService<ILogger<Program>>()));
builder.Services.AddSingleton<ContentServices>();
builder.Services.AddDriveInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseForwardedHeaders();
app.UsePortSeparation(ports);
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/config", (IConfiguration cfg) => Results.Ok(new
{
    participantId = cfg["Drive:Participant:Id"],
    bpn = cfg["Drive:Participant:Bpn"],
    dspAddress = cfg["Drive:Participant:DspAddress"],
    edcProfile = cfg["Drive:Edc:VersionProfile"],
}));

app.MapContentEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
