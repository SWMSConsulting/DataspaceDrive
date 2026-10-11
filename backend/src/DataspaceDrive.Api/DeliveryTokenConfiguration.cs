using System.Globalization;
using System.Security.Cryptography;
using DataspaceDrive.Core.Shares;

namespace DataspaceDrive.Api;

/// <summary>
/// Liest den Schlüsselring der Zustell-Tokens, z. B. <c>Drive__DeliveryTokens__Keys__1=&lt;base64&gt;</c> und
/// <c>Drive__DeliveryTokens__CurrentVersion=1</c>. Ohne Schlüssel startet die Anwendung nur in der
/// Entwicklungsumgebung, dann mit einem flüchtigen Zufallsschlüssel.
/// </summary>
public static class DeliveryTokenConfiguration
{
    public static DeliveryTokenService Create(IConfiguration section, IHostEnvironment environment, ILogger logger)
    {
        var keys = new Dictionary<int, byte[]>();
        foreach (var child in section.GetSection("Keys").GetChildren())
        {
            if (!int.TryParse(child.Key.TrimStart('v'), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                throw new InvalidOperationException($"Ungültige Schlüsselversion '{child.Key}' unter Drive:DeliveryTokens:Keys.");
            keys[version] = Convert.FromBase64String(child.Value ?? "");
        }

        if (keys.Count == 0)
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Drive:DeliveryTokens:Keys ist nicht konfiguriert.");
            logger.LogWarning("Kein Schlüssel für Zustell-Tokens konfiguriert; verwende einen flüchtigen Entwicklungsschlüssel.");
            keys[1] = RandomNumberGenerator.GetBytes(32);
        }

        var current = section.GetValue<int?>("CurrentVersion") ?? keys.Keys.Max();
        return new DeliveryTokenService(keys, current);
    }
}
