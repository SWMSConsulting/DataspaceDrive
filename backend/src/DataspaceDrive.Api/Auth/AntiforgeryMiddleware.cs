using Microsoft.AspNetCore.Antiforgery;

namespace DataspaceDrive.Api.Auth;

/// <summary>
/// Schutz vor Cross-Site-Requests für die Cookie-Sitzung: GET-Requests der API erneuern das lesbare Cookie
/// <c>XSRF-TOKEN</c>, ändernde Requests müssen den Wert im Header <c>X-XSRF-TOKEN</c> zurückschicken
/// (Angular macht das über withXsrfConfiguration automatisch).
/// </summary>
public static class AntiforgeryMiddleware
{
    public const string CookieName = "XSRF-TOKEN";
    public const string HeaderName = "X-XSRF-TOKEN";

    public static IServiceCollection AddDriveAntiforgery(this IServiceCollection services) =>
        services.AddAntiforgery(o =>
        {
            o.HeaderName = HeaderName;
            o.Cookie.Name = "__Host-drive-af";
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });

    public static IApplicationBuilder UseDriveAntiforgery(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth") && context.User.Identity?.IsAuthenticated == true)
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                {
                    var tokens = antiforgery.GetAndStoreTokens(context);
                    context.Response.Cookies.Append(CookieName, tokens.RequestToken!, new CookieOptions
                    {
                        HttpOnly = false, Secure = true, SameSite = SameSiteMode.Strict, Path = "/",
                    });
                }
                else
                {
                    await antiforgery.ValidateRequestAsync(context);
                }
            }
            await next(context);
        });
}
