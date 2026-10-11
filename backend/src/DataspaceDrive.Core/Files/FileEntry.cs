namespace DataspaceDrive.Core.Files;

public sealed class FileEntry
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public Guid FolderId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public Guid? DeletedRootId { get; set; }

    /// <summary>Optimistische Sperre; wird bei jeder Änderung neu vergeben.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.NewGuid();
}
