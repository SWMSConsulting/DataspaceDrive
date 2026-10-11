using System.Security.Claims;
using System.Text.Encodings.Web;
using DataspaceDrive.Core.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace DataspaceDrive.Api.Auth;

public sealed class OidcSettings
{
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Claim mit den Rollen; der windx-Keycloak-Realm bildet die Realm-Rollen flach auf „roles“ ab.</summary>
    public string RoleClaim { get; set; } = "roles";

    public string GroupClaim { get; set; } = "groups";
    public bool RequireHttpsMetadata { get; set; } = true;
}

public static class AuthSetup
{
    public const string UserPolicy = "drive-user";
    public const string AdminPolicy = "drive-admin";
    public const string DevelopmentScheme = "Development";

    /// <summary>
    /// BFF-Anmeldung: Der Browser hält nur ein HttpOnly-Cookie, Tokens bleiben auf dem Server. Im Modus
    /// „Development“ meldet ein fester Testnutzer an; dieser Modus ist außerhalb der Entwicklungsumgebung gesperrt.
    /// Die Konfiguration wird erst beim Auflösen der Optionen gelesen, damit Overrides (z. B. in Tests) greifen.
    /// </summary>
    public static IServiceCollection AddDriveAuthentication(this IServiceCollection services)
    {
        services.AddOptions<OidcSettings>().BindConfiguration("Auth:Oidc");
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OidcSettings>>().Value);

        services.AddAuthorizationBuilder()
            .AddPolicy(UserPolicy, p => p.RequireAuthenticatedUser().RequireRole(DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin))
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(DrivePrincipal.RoleAdmin));

        services.AddAuthentication()
            .AddCookie(o =>
            {
                o.Cookie.Name = "__Host-drive";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect()
            .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthHandler>(DevelopmentScheme, _ => { });

        services.AddOptions<AuthenticationOptions>().Configure<IConfiguration, IHostEnvironment>((o, configuration, environment) =>
        {
            if (configuration["Auth:Mode"] == DevelopmentScheme)
            {
                if (!environment.IsDevelopment())
                    throw new InvalidOperationException("Auth:Mode=Development ist nur in der Entwicklungsumgebung erlaubt.");
                o.DefaultScheme = DevelopmentScheme;
                o.DefaultChallengeScheme = DevelopmentScheme;
                return;
            }
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        });

        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme).Configure<IOptions<OidcSettings>>((o, settings) =>
        {
            var oidc = settings.Value;
            o.Authority = oidc.Authority;
            o.ClientId = oidc.ClientId;
            o.ClientSecret = oidc.ClientSecret;
            o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.UsePkce = true;
            o.SaveTokens = true; // id_token für den Logout beim IdP
            o.GetClaimsFromUserInfoEndpoint = true;
            o.MapInboundClaims = false;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = oidc.RoleClaim;
            o.ClaimActions.MapJsonKey(oidc.RoleClaim, oidc.RoleClaim);
            o.ClaimActions.MapJsonKey(oidc.GroupClaim, oidc.GroupClaim);
            o.Events.OnRedirectToIdentityProvider = ctx =>
            {
                // API-Aufrufe ohne Sitzung bekommen 401; die SPA leitet dann selbst zur Anmeldung.
                if (ctx.Request.Path.StartsWithSegments("/api") && !ctx.Request.Path.StartsWithSegments("/api/auth"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.HandleResponse();
                }
                return Task.CompletedTask;
            };
        });
        return services;
    }

    /// <summary>Prüft nach dem Aufbau der Konfiguration, dass die Anmeldung vollständig eingerichtet ist.</summary>
    public static void ValidateAuthConfiguration(IConfiguration configuration)
    {
        if (configuration["Auth:Mode"] == DevelopmentScheme)
            return;
        if (string.IsNullOrWhiteSpace(configuration["Auth:Oidc:Authority"]) || string.IsNullOrWhiteSpace(configuration["Auth:Oidc:ClientId"]))
            throw new InvalidOperationException("Auth:Oidc:Authority und Auth:Oidc:ClientId müssen gesetzt sein.");
    }
}

/// <summary>Fester Testnutzer für die lokale Entwicklung (Auth:Development:*).</summary>
public sealed class DevelopmentAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration,
    IHostEnvironment environment) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment())
            return Task.FromResult(AuthenticateResult.NoResult());
        var section = configuration.GetSection("Auth:Development");
        var roles = section.GetSection("Roles").Get<string[]>()
                    ?? [DrivePrincipal.RoleUser, DrivePrincipal.RoleExternalShare, DrivePrincipal.RoleInbox, DrivePrincipal.RoleAdmin];
        var claims = new List<Claim>
        {
            new("sub", section["Subject"] ?? "dev-user"),
            new("name", section["Name"] ?? "Entwicklung"),
            new("email", section["Email"] ?? "dev@example.org"),
        };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        var identity = new ClaimsIdentity(claims, Scheme.Name, "name", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
