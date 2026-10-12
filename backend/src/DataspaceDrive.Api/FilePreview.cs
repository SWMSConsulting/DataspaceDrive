using Microsoft.Net.Http.Headers;

namespace DataspaceDrive.Api;

/// <summary>
/// Vorschau im Browser nur für Typen, die keinen Code im Kontext der Anwendung ausführen können: Rastergrafiken, PDF und
/// Klartext. SVG und HTML bleiben Downloads. Bilder und Text bekommen zusätzlich eine sandboxende CSP, falls jemand
/// die Adresse direkt öffnet; PDF braucht den eingebauten Betrachter des Browsers und darf nur eingebettet werden.
/// </summary>
public static class FilePreview
{
    public enum Kind
    {
        Image,
        Pdf,
        Text,
    }

    public const long MaxTextBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    public static Kind? KindOf(string contentType)
    {
        var type = contentType.Split(';')[0].Trim();
        if (Images.Contains(type))
            return Kind.Image;
        if (string.Equals(type, "application/pdf", StringComparison.OrdinalIgnoreCase))
            return Kind.Pdf;
        if (string.Equals(type, "text/plain", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "text/csv", StringComparison.OrdinalIgnoreCase))
            return Kind.Text;
        return null;
    }

    public static void ApplyHeaders(HttpResponse response, Kind kind, string fileName)
    {
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(fileName);
        response.Headers.ContentDisposition = disposition.ToString();
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.CacheControl = "private, no-store";
        response.Headers.ContentSecurityPolicy = kind == Kind.Pdf
            ? "frame-ancestors 'self'"
            : "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'self'; sandbox";
    }
}
