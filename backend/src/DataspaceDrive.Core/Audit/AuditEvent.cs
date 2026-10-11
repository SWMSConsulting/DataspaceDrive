namespace DataspaceDrive.Core.Audit;

/// <summary>Protokolleintrag; wird nur angefügt, nie geändert.</summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTime OccurredAt { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
}
