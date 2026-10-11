namespace DataspaceDrive.Core.Users;

/// <summary>Nutzer dieser Instanz; wird beim ersten Login aus dem OIDC-Subject angelegt.</summary>
public sealed class DriveUser
{
    public Guid Id { get; set; }
    public required string Subject { get; set; }
    public string? Email { get; set; }
    public required string DisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;
}
