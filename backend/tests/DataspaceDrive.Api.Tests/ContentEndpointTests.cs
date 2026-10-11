using System.Net;
using DataspaceDrive.Core.Shares;

namespace DataspaceDrive.Api.Tests;

public sealed class ContentEndpointTests : IClassFixture<DriveApiFactory>
{
    private const string Recipient = "BPNL00000000WA01";
    private readonly DriveApiFactory _factory;
    private readonly DeliveryTokenService _tokens = new(new Dictionary<int, byte[]> { [1] = DriveApiFactory.TokenKey }, 1);

    public ContentEndpointTests(DriveApiFactory factory) => _factory = factory;

    private async Task<HttpResponseMessage> SendAsync(string path, int port, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Test-Local-Port", port.ToString());
        configure?.Invoke(request);
        return await _factory.CreateClient().SendAsync(request);
    }

    [Theory]
    [InlineData(DriveApiFactory.AppPort)]
    [InlineData(DriveApiFactory.ContentPort)]
    public async Task Healthz_answers_on_both_ports(int port) =>
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("/healthz", port)).StatusCode);

    [Fact]
    public async Task App_api_is_not_reachable_on_the_content_port() =>
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("/api/config", DriveApiFactory.ContentPort)).StatusCode);

    [Fact]
    public async Task Content_endpoint_is_not_reachable_on_the_app_port()
    {
        var response = await SendAsync("/edc/deliveries/d-1", DriveApiFactory.AppPort,
            r => r.Headers.Add("X-Drive-Delivery-Token", _tokens.Create("d-1")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_unauthorized() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync("/edc/deliveries/d-1", DriveApiFactory.ContentPort)).StatusCode);

    [Fact]
    public async Task Token_of_another_delivery_is_unauthorized()
    {
        var response = await SendAsync("/edc/deliveries/d-1", DriveApiFactory.ContentPort,
            r => r.Headers.Add("X-Drive-Delivery-Token", _tokens.Create("d-2")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Valid_token_for_unknown_delivery_is_not_found()
    {
        var response = await SendAsync("/edc/deliveries/unknown", DriveApiFactory.ContentPort,
            r => r.Headers.Add("X-Drive-Delivery-Token", _tokens.Create("unknown")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Recipient_with_matching_agreement_receives_the_file_and_others_are_refused()
    {
        var content = "Prüfbericht WEA 17"u8.ToArray();
        await _factory.Blobs.WriteAsync("blobs/d-ok/v1", new MemoryStream(content));
        _factory.Directory.Deliveries["d-ok"] = new DeliverySnapshot("d-ok", DeliveryState.Active,
            DateTimeOffset.UtcNow.AddDays(1), Recipient, PartnerBlocked: false, "drive-d-d-ok");
        _factory.Directory.Agreements["agr-ok"] = new AgreementSnapshot("agr-ok", "drive-d-d-ok", Retired: false);
        _factory.Directory.Items["d-ok"] = new DeliveryItem("1", "blobs/d-ok/v1", "Prüfbericht.pdf", "application/pdf", content.Length);

        var ok = await SendAsync("/edc/deliveries/d-ok", DriveApiFactory.ContentPort, r =>
        {
            r.Headers.Add("X-Drive-Delivery-Token", _tokens.Create("d-ok"));
            r.Headers.Add("Edc-Bpn", Recipient);
            r.Headers.Add("Edc-Contract-Agreement-Id", "agr-ok");
        });
        var otherCompany = await SendAsync("/edc/deliveries/d-ok", DriveApiFactory.ContentPort, r =>
        {
            r.Headers.Add("X-Drive-Delivery-Token", _tokens.Create("d-ok"));
            r.Headers.Add("Edc-Bpn", "BPNL00000000WD04");
            r.Headers.Add("Edc-Contract-Agreement-Id", "agr-ok");
        });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(content, await ok.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden, otherCompany.StatusCode);
    }

    [Fact]
    public async Task Revoked_delivery_is_gone()
    {
        _factory.Directory.Deliveries["d-revoked"] = new DeliverySnapshot("d-revoked", DeliveryState.Revoked,
            DateTimeOffset.UtcNow.AddDays(1), Recipient, PartnerBlocked: false, "drive-d-d-revoked");

        var response = await SendAsync("/edc/deliveries/d-revoked", DriveApiFactory.ContentPort, r =>
        {
            r.Headers.Add("X-Drive-Delivery-Token", _tokens.Create("d-revoked"));
            r.Headers.Add("Edc-Bpn", Recipient);
        });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }
}
