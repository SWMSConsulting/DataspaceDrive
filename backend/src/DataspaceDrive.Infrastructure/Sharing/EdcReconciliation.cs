using System.Text.Json.Nodes;
using System.Threading.Channels;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swms.Edc.Client;
using Swms.Edc.Client.Policies;

namespace DataspaceDrive.Infrastructure.Sharing;

public enum FindingSeverity
{
    Critical,
    Warning,
    Info,
}

public sealed record EdcFinding(FindingSeverity Severity, string ObjectId, string Message);

public sealed record EdcStatus(
    DateTime? CheckedAt,
    bool Reachable,
    string? Error,
    int ContractDefinitions,
    int ActiveDeliveries,
    IReadOnlyList<EdcFinding> Findings);

/// <summary>
/// Gleicht die Contract Definitions des eigenen Connectors mit den Zustellungen ab. Kritisch sind Definitionen mit
/// leerem Asset-Selektor: sie geben jedes Asset des Connectors für alle frei, die die Access-Policy erfüllen, und
/// hebeln damit den Katalogschutz der Zustellungen aus (im Spike beobachtet). Verwaiste <c>drive-cd-*</c> ohne
/// aktive Zustellung werden entfernt.
/// </summary>
public sealed class EdcReconciliation(IServiceScopeFactory scopes, TimeProvider clock, ILogger<EdcReconciliation> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    private readonly Channel<bool> _trigger = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private volatile EdcStatus _status = new(null, false, null, 0, 0, []);

    public EdcStatus Status => _status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!DeliveryWorker.WorkersEnabled(scopes))
            return;
        var delay = StartDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wait.CancelAfter(delay);
                try
                {
                    await _trigger.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Intervall abgelaufen
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Abgleich mit dem Connector fehlgeschlagen");
            }
            delay = Interval;
        }
    }

    public void RequestRun() => _trigger.Writer.TryWrite(true);

    /// <summary>Führt den Abgleich aus und merkt sich das Ergebnis.</summary>
    public async Task<EdcStatus> RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var connection = scope.ServiceProvider.GetRequiredService<EdcConnection>();
        var now = clock.GetUtcNow().UtcDateTime;
        if (!connection.IsConfigured)
            return _status = new EdcStatus(now, false, "Kein EDC konfiguriert.", 0, 0, []);
        var edc = connection.Client;
        IReadOnlyList<JsonObject> definitions;
        try
        {
            definitions = await edc.QueryContractDefinitionsAsync(ct);
        }
        catch (Exception ex) when (ex is EdcException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return _status = new EdcStatus(now, false, ex.Message, 0, 0, []);
        }

        var findings = new List<EdcFinding>();
        var activeDeliveries = await db.Deliveries.Where(d => d.State == DeliveryState.Active).Select(d => d.Id).ToListAsync(ct);
        var deliveryStates = await db.Deliveries.Select(d => new { d.Id, d.State }).ToDictionaryAsync(d => d.Id.ToString("N"), d => d.State, ct);
        var driveDefinitions = new HashSet<string>();
        foreach (var definition in definitions)
        {
            var id = JsonLd.Id(definition) ?? "?";
            var selector = JsonLd.Objects(JsonLd.Get(definition, "assetsSelector"));
            var accessPolicy = JsonLd.GetString(definition, "accessPolicyId");
            if (id.StartsWith("drive-cd-", StringComparison.Ordinal))
            {
                driveDefinitions.Add(id);
                var deliveryId = id["drive-cd-".Length..];
                if (!deliveryStates.TryGetValue(deliveryId, out var state) || DeliveryLifecycle.IsFinal(state))
                {
                    await edc.DeleteContractDefinitionAsync(id, ct);
                    findings.Add(new EdcFinding(FindingSeverity.Info, id, "Verwaiste Contract Definition ohne aktive Zustellung entfernt."));
                }
                continue;
            }
            if (selector.Count == 0)
            {
                findings.Add(new EdcFinding(FindingSeverity.Critical, id,
                    $"Kein Asset-Selektor: gibt alle Assets des Connectors frei, auch Zustellungen (Access-Policy {accessPolicy}). Selektor einschränken."));
                continue;
            }
            if (selector.Any(MayMatchDeliveries))
                findings.Add(new EdcFinding(FindingSeverity.Warning, id,
                    $"Breiter Asset-Selektor ({Describe(selector)}); prüfen, ob er Zustellungen (drive-d-*) erfasst."));
        }
        foreach (var deliveryId in activeDeliveries)
        {
            var cdId = DriveExchange.ContractDefinitionId(deliveryId.ToString("N"));
            if (!driveDefinitions.Contains(cdId))
                findings.Add(new EdcFinding(FindingSeverity.Warning, cdId, "Die Zustellung ist aktiv, ihre Contract Definition fehlt im Connector."));
        }
        foreach (var finding in findings.Where(f => f.Severity == FindingSeverity.Critical))
            logger.LogWarning("Connector-Abgleich: {ObjectId}: {Message}", finding.ObjectId, finding.Message);
        return _status = new EdcStatus(now, true, null, definitions.Count, activeDeliveries.Count, findings);
    }

    /// <summary>Unbedenklich sind nur Selektoren auf feste Asset-IDs, die keine Zustellung sind.</summary>
    private static bool MayMatchDeliveries(JsonObject criterion)
    {
        var left = JsonLd.GetString(criterion, "operandLeft") ?? "";
        var op = JsonLd.GetString(criterion, "operator") ?? "";
        var right = JsonLd.Get(criterion, "operandRight");
        var onId = left == "id" || left.EndsWith("/id", StringComparison.Ordinal) || left.EndsWith(":id", StringComparison.Ordinal);
        if (!onId || op is not ("=" or "in"))
            return true;
        var values = right is JsonArray array ? array.Select(JsonLd.AsString) : [JsonLd.AsString(right)];
        return values.Any(v => v is null || v.StartsWith("drive-d-", StringComparison.Ordinal));
    }

    private static string Describe(IReadOnlyList<JsonObject> selector) => string.Join(" und ", selector.Select(c =>
        $"{JsonLd.GetString(c, "operandLeft")} {JsonLd.GetString(c, "operator")} {JsonLd.Get(c, "operandRight")?.ToJsonString()}"));
}
