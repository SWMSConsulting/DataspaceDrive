using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using DataspaceDrive.Core.Scanning;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Infrastructure.Scanning;

public sealed class ClamAvOptions
{
    /// <summary>clamd-Host; leer = Virenscan abgeschaltet (nur Entwicklung).</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 3310;

    public int ChunkSize { get; set; } = 64 * 1024;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Muss zu <c>StreamMaxLength</c> in clamd.conf passen (2047 MiB); ClamAV prüft höchstens 2 GiB - 1 Byte,
    /// größere Dateien gelten als nicht prüfbar.
    /// </summary>
    public long MaxStreamBytes { get; set; } = 2047L * 1024 * 1024;
}

/// <summary>Prüft Inhalte über das clamd-Protokoll INSTREAM (Teilstücke mit 4-Byte-Längenpräfix).</summary>
public sealed class ClamAvScanner(IOptions<ClamAvOptions> options) : IVirusScanner
{
    private readonly ClamAvOptions _options = options.Value;

    public bool Enabled => true;

    public long? MaxBytes => _options.MaxStreamBytes;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(_options.Host!, _options.Port, timeout.Token);
        await using var network = client.GetStream();

        await network.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token);
        var buffer = new byte[_options.ChunkSize];
        var prefix = new byte[4];
        long total = 0;
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
                if (total > _options.MaxStreamBytes)
                    return new ScanResult(ScanVerdict.NotScannable, $"Datei größer als {_options.MaxStreamBytes} Bytes");
                BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)read);
                await network.WriteAsync(prefix, timeout.Token);
                await network.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            BinaryPrimitives.WriteUInt32BigEndian(prefix, 0);
            await network.WriteAsync(prefix, timeout.Token);
        }
        catch (IOException)
        {
            // clamd schließt die Verbindung bei Überschreitung von StreamMaxLength; die Antwort erklärt den Grund.
        }
        return Parse(await ReadReplyAsync(network, timeout.Token));
    }

    internal static ScanResult Parse(string reply)
    {
        var text = reply.Trim('\0', ' ', '\r', '\n');
        if (text.EndsWith(" OK", StringComparison.Ordinal))
            return new ScanResult(ScanVerdict.Clean);
        if (text.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var signature = text[..^" FOUND".Length];
            var colon = signature.IndexOf(": ", StringComparison.Ordinal);
            signature = colon >= 0 ? signature[(colon + 2)..] : signature;
            // Mit AlertExceedsMax meldet clamd überschrittene Limits als Fund; das ist kein Virus, aber ungeprüft.
            return signature.StartsWith("Heuristics.Limits.Exceeded", StringComparison.Ordinal)
                ? new ScanResult(ScanVerdict.NotScannable, signature)
                : new ScanResult(ScanVerdict.Infected, signature);
        }
        if (text.Contains("size limit exceeded", StringComparison.OrdinalIgnoreCase))
            return new ScanResult(ScanVerdict.NotScannable, "Größenlimit von clamd überschritten");
        return new ScanResult(ScanVerdict.Error, text.Length > 0 ? text : "keine Antwort von clamd");
    }

    private static async Task<string> ReadReplyAsync(NetworkStream network, CancellationToken ct)
    {
        var reply = new StringBuilder();
        var buffer = new byte[256];
        try
        {
            int read;
            while ((read = await network.ReadAsync(buffer, ct)) > 0)
            {
                reply.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
                    break;
            }
        }
        catch (IOException)
        {
            // Verbindung beendet: es zählt, was bis dahin gelesen wurde.
        }
        return reply.ToString();
    }
}

/// <summary>Kein Scanner konfiguriert: Dateien bleiben als ungeprüft markiert (nur für die Entwicklung gedacht).</summary>
public sealed class DisabledVirusScanner : IVirusScanner
{
    public bool Enabled => false;

    public long? MaxBytes => null;

    public Task<ScanResult> ScanAsync(Stream content, CancellationToken ct) =>
        throw new InvalidOperationException("Der Virenscan ist nicht konfiguriert.");
}
