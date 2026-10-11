using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DataspaceDrive.Core.Scanning;
using DataspaceDrive.Infrastructure.Scanning;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Core.Tests.Scanning;

public class ClamAvScannerTests
{
    [Theory]
    [InlineData("stream: OK\0", ScanVerdict.Clean, null)]
    [InlineData("stream: Eicar-Test-Signature FOUND\0", ScanVerdict.Infected, "Eicar-Test-Signature")]
    [InlineData("INSTREAM size limit exceeded. ERROR\0", ScanVerdict.NotScannable, "Größenlimit von clamd überschritten")]
    [InlineData("stream: Can't allocate memory ERROR\0", ScanVerdict.Error, "stream: Can't allocate memory ERROR")]
    [InlineData("", ScanVerdict.Error, "keine Antwort von clamd")]
    public void Replies_are_parsed(string reply, ScanVerdict verdict, string? detail)
    {
        var result = ClamAvScanner.Parse(reply);

        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(detail, result.Detail);
    }

    [Fact]
    public async Task Instream_protocol_against_a_fake_clamd()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new MemoryStream();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));
            var prefix = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(prefix);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(prefix);
                if (length == 0)
                    break;
                var chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk);
                received.Write(chunk);
            }
            var verdict = Encoding.ASCII.GetString(received.ToArray()).Contains("EICAR") ? "stream: Eicar-Test-Signature FOUND\0" : "stream: OK\0";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(verdict));
        });

        var scanner = new ClamAvScanner(Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = port, ChunkSize = 7 }));
        var result = await scanner.ScanAsync(new MemoryStream("Inhalt mit EICAR-Kennung"u8.ToArray()), CancellationToken.None);
        await server;

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("Inhalt mit EICAR-Kennung", Encoding.ASCII.GetString(received.ToArray()));
    }

    [Fact]
    public async Task Content_over_the_configured_limit_is_not_scannable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[1024];
            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });

        var scanner = new ClamAvScanner(Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = port, MaxStreamBytes = 10 }));
        var result = await scanner.ScanAsync(new MemoryStream(new byte[100]), CancellationToken.None);

        Assert.Equal(ScanVerdict.NotScannable, result.Verdict);
        listener.Stop();
        await Task.WhenAny(server, Task.Delay(1000));
    }
}
