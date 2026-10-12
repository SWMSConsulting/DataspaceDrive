namespace DataspaceDrive.Infrastructure.Notifications;

/// <summary>SMTP-Versand (<c>Drive:Mail</c>); ohne Host verschickt die Drive keine E-Mails.</summary>
public sealed class MailOptions
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 25;
    public bool UseStartTls { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "DataspaceDrive <noreply@localhost>";

    /// <summary>Öffentliche Adresse der Oberfläche für Links in den E-Mails.</summary>
    public string PublicUrl { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost);
}
