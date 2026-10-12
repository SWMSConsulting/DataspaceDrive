using DataspaceDrive.Core.Access;

namespace DataspaceDrive.Core.Settings;

/// <summary>Einstellung dieser Instanz, die die Verwaltung zur Laufzeit ändert.</summary>
public sealed class InstanceSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime ChangedAt { get; set; }
    public Guid? ChangedBy { get; set; }
}

/// <param name="ContractProfile">Vertragsprofil aus der Konfiguration (nur zur Anzeige).</param>
public sealed record SharingSettings(bool RequireApproval, string? ContractProfile = null);

public interface IInstanceSettings
{
    Task<SharingSettings> SharingAsync(CancellationToken ct);

    Task<SharingSettings> UpdateSharingAsync(DrivePrincipal admin, SharingSettings settings, CancellationToken ct);
}
