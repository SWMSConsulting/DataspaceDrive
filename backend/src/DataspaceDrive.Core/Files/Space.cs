namespace DataspaceDrive.Core.Files;

public enum SpaceKind
{
    Personal,
    Team,
}

/// <summary>Bereich mit eigenem Ordnerbaum: persönlich (ein Besitzer) oder Team (Rechte über ACL).</summary>
public sealed class Space
{
    public Guid Id { get; set; }
    public SpaceKind Kind { get; set; }
    public required string Name { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid RootFolderId { get; set; }
    public bool ExternalSharingAllowed { get; set; }
    public DateTime CreatedAt { get; set; }
}
