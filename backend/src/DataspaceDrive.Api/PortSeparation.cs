namespace DataspaceDrive.Api;

public sealed class DrivePorts
{
    public int App { get; set; } = 8080;

    public int Content { get; set; } = 8090;
}

public static class PortSeparation
{
    /// <summary>
    /// Auf dem Content-Port nur /edc/*, auf dem App-Port nie /edc/*. /healthz ist auf beiden Ports erreichbar,
    /// damit Probes und die Dataplane-Erreichbarkeit getrennt prüfbar bleiben.
    /// </summary>
    public static IApplicationBuilder UsePortSeparation(this IApplicationBuilder app, DrivePorts ports) =>
        app.Use(async (context, next) =>
        {
            var onContentPort = context.Connection.LocalPort == ports.Content;
            var isContentPath = context.Request.Path.StartsWithSegments("/edc");
            if (!context.Request.Path.StartsWithSegments("/healthz") && onContentPort != isContentPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next(context);
        });
}
