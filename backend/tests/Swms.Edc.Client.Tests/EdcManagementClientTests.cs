using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Swms.Edc.Client.Tests;

public class EdcManagementClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            var (status, text) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static (EdcManagementClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://edc.local/management/") };
        return (new EdcManagementClient(http, EdcVersionProfile.Tractusx012), handler);
    }

    [Fact]
    public async Task Existing_objects_count_as_created()
    {
        var (client, handler) = Create((_, _) => (HttpStatusCode.Conflict, """[{"message":"exists"}]"""));

        Assert.Equal(CreateOutcome.AlreadyExists, await client.CreatePolicyAsync(new JsonObject { ["@id"] = "p" }, CancellationToken.None));
        Assert.Equal("/management/v3/policydefinitions", handler.Requests.Single().Path);
    }

    [Fact]
    public async Task Errors_carry_status_and_operation()
    {
        var (client, _) = Create((_, _) => (HttpStatusCode.BadRequest, """[{"message":"invalid"}]"""));

        var error = await Assert.ThrowsAsync<EdcException>(() => client.CreateAssetAsync(new JsonObject(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, error.Status);
        Assert.Contains("Asset anlegen", error.Message);
    }

    [Fact]
    public async Task Discovery_returns_the_did_resolved_by_the_connector()
    {
        var (client, handler) = Create((_, _) => (HttpStatusCode.OK, """
            {"protocol":"dataspace-protocol-http:2025-1","counterPartyAddress":"https://bob/api/v1/dsp/2025-1",
             "counterPartyId":"did:web:bob","@context":{"@vocab":"https://w3id.org/edc/v0.0.1/ns/"}}
            """));

        var dsp = await client.DiscoverDspAsync("BPNL00000000WB02", "https://bob/api/v1/dsp", CancellationToken.None);

        Assert.Equal(new DspParams("dataspace-protocol-http:2025-1", "https://bob/api/v1/dsp/2025-1", "did:web:bob"), dsp);
        Assert.Equal("/management/v4alpha/connectordiscovery/dspversionparams", handler.Requests.Single().Path);
    }

    [Fact]
    public async Task Agreements_are_read_with_prefixed_properties()
    {
        var (client, _) = Create((_, _) => (HttpStatusCode.OK, """
            {"@id":"agr-1","edc:assetId":"drive-d-1","edc:consumerId":"did:web:bob","edc:providerId":"did:web:alice"}
            """));

        var agreement = await client.GetAgreementAsync("agr-1", CancellationToken.None);

        Assert.Equal(new ContractAgreementInfo("agr-1", "drive-d-1", "did:web:bob", "did:web:alice"), agreement);
    }

    [Fact]
    public async Task Missing_agreement_is_null_and_repeated_retirement_is_no_error()
    {
        var (client, _) = Create((request, _) => request.Method == HttpMethod.Get
            ? (HttpStatusCode.NotFound, "[]")
            : (HttpStatusCode.Conflict, """[{"message":"already retired"}]"""));

        Assert.Null(await client.GetAgreementAsync("unbekannt", CancellationToken.None));
        await client.RetireAgreementAsync("agr-1", "Widerruf", CancellationToken.None);
    }

    [Fact]
    public async Task Edr_flow_reads_transfer_and_data_address()
    {
        var (client, handler) = Create((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/management/v3/edrs" => (HttpStatusCode.OK, """{"@id":"neg-1"}"""),
            "/management/v3/contractnegotiations/neg-1" => (HttpStatusCode.OK, """{"@id":"neg-1","state":"FINALIZED","contractAgreementId":"agr-9"}"""),
            "/management/v3/edrs/request" => (HttpStatusCode.OK, """[{"@id":"tp-1","transferProcessId":"tp-1","contractNegotiationId":"neg-1"}]"""),
            "/management/v3/edrs/tp-1/dataaddress" => (HttpStatusCode.OK, """{"endpoint":"https://alice-dp/api/public","authorization":"eyJ"}"""),
            _ => (HttpStatusCode.NotFound, "[]"),
        });

        var negotiationId = await client.StartEdrNegotiationAsync(new JsonObject(), CancellationToken.None);
        var negotiation = await client.GetNegotiationAsync(negotiationId, CancellationToken.None);
        var transfer = await client.FindEdrTransferProcessAsync(negotiationId, CancellationToken.None);
        var address = await client.GetEdrDataAddressAsync(transfer!, CancellationToken.None);

        Assert.True(negotiation.IsFinalized);
        Assert.Equal("agr-9", negotiation.AgreementId);
        Assert.Equal(new EdrDataAddress("https://alice-dp/api/public", "eyJ"), address);
        Assert.Contains(handler.Requests, r => r.Path == "/management/v3/edrs/tp-1/dataaddress?auto_refresh=true");
        Assert.Contains("contractNegotiationId", handler.Requests.Single(r => r.Path == "/management/v3/edrs/request").Body);
    }
}
