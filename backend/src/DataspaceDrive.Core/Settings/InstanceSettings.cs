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

public sealed record SharingSettings(bool RequireApproval);

public interface IInstanceSettings
{
    Task<SharingSettings> SharingAsync(CancellationToken ct);

    Task<SharingSettings> UpdateSharingAsync(DrivePrincipal admin, SharingSettings settings, CancellationToken ct);
}
