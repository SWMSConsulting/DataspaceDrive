using DataspaceDrive.Core.Access;

namespace DataspaceDrive.Core.Notifications;

/// <summary>Ausgehende E-Mail (Outbox); der Versand läuft im Hintergrund mit Wiederholungen.</summary>
public sealed class MailMessage
{
    public long Id { get; set; }
    public required string To { get; set; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Zähler für die Navigation; null, wenn die Rolle fehlt.</summary>
public sealed record NotificationSummary(int? InboxNew, int? InboxAttention, int? ApprovalsPending, int? DeliveriesFailed);

public interface INotificationService
{
    Task<NotificationSummary> SummaryAsync(DrivePrincipal principal, CancellationToken ct);
}
