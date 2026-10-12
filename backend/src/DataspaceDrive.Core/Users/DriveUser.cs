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

    /// <summary>Zuletzt gesehene IdP-Rollen als „,rolle1,rolle2,“ (für Benachrichtigungen an Rolleninhaber).</summary>
    public string? Roles { get; set; }

    /// <summary>E-Mail-Benachrichtigungen; jede Person kann sie in den Einstellungen abschalten.</summary>
    public bool EmailNotifications { get; set; } = true;

    public static string RoleMarker(string role) => "," + role + ",";
}
