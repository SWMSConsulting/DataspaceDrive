using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataspaceDrive.Core.Shares;

/// <summary>
/// Zustell-Token, das die EDC-Dataplane als <c>authCode</c> an den Content-Endpunkt mitschickt:
/// <c>v{n}.</c> + base64url(HMAC-SHA256(Schlüssel n, Zustell-ID)). Das Token steht im Klartext in der
/// dataAddress des Assets, gilt aber nur für genau eine Zustellung. Die Schlüssel sind versioniert, damit
/// eine Rotation bereits veröffentlichte Zustellungen nicht bricht; ein Vault ist dafür nicht nötig.
/// </summary>
public sealed class DeliveryTokenService
{
    private const int MinimumKeyLength = 32;

    private readonly IReadOnlyDictionary<int, byte[]> _keys;
    private readonly int _currentVersion;

    public DeliveryTokenService(IReadOnlyDictionary<int, byte[]> keys, int currentVersion)
    {
        if (!keys.ContainsKey(currentVersion))
            throw new ArgumentException($"Für die aktuelle Version v{currentVersion} ist kein Schlüssel hinterlegt.", nameof(keys));
        foreach (var (version, key) in keys)
        {
            if (version < 1)
                throw new ArgumentException("Schlüsselversionen beginnen bei 1.", nameof(keys));
            if (key.Length < MinimumKeyLength)
                throw new ArgumentException($"Schlüssel v{version} ist kürzer als {MinimumKeyLength} Bytes.", nameof(keys));
        }
        _keys = keys;
        _currentVersion = currentVersion;
    }

    public string Create(string deliveryId) => Create(deliveryId, _currentVersion);

    public bool Verify(string deliveryId, string? token)
    {
        if (string.IsNullOrEmpty(token) || token[0] != 'v')
            return false;
        var dot = token.IndexOf('.');
        if (dot < 2 || !int.TryParse(token.AsSpan(1, dot - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || !_keys.ContainsKey(version))
            return false;
        var expected = Encoding.UTF8.GetBytes(Create(deliveryId, version));
        var actual = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Create(string deliveryId, int version)
    {
        var mac = HMACSHA256.HashData(_keys[version], Encoding.UTF8.GetBytes(deliveryId));
        return $"v{version.ToString(CultureInfo.InvariantCulture)}.{Base64Url.EncodeToString(mac)}";
    }
}
