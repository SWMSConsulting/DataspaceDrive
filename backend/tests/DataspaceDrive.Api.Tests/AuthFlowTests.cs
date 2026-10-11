using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DataspaceDrive.Api.Tests;

/// <summary>Anmeldeablauf mit der echten Cookie-/OIDC-Konfiguration (ohne erreichbaren IdP).</summary>
public sealed class AuthFlowTests(RealAuthApiFactory factory) : IClassFixture<RealAuthApiFactory>
{
    private HttpClient Client()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add("X-Test-Local-Port", DriveApiFactory.AppPort.ToString());
        return client;
    }

    [Fact]
    public async Task Api_calls_without_session_get_401_and_leave_no_cookies()
    {
        var response = await Client().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_redirects_to_the_identity_provider_with_one_correlation_cookie()
    {
        var response = await Client().GetAsync("/api/auth/login?returnUrl=%2Ftrash");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(DriveApiFactory.IdpAuthorizationEndpoint, response.Headers.Location!.ToString());
        var cookies = response.Headers.GetValues("Set-Cookie").Select(c => c.Split('=')[0]).ToList();
        Assert.Single(cookies, c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal));
        Assert.Single(cookies, c => c.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_ignores_external_return_urls()
    {
        var response = await Client().GetAsync("/api/auth/login?returnUrl=https%3A%2F%2Fevil.example%2F");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("evil.example", Uri.UnescapeDataString(response.Headers.Location!.ToString()));
    }
}
