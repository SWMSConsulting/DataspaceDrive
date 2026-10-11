namespace DataspaceDrive.Core.Files;

public sealed class Folder
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }

    /// <summary>Null nur beim Wurzelordner eines Bereichs.</summary>
    public Guid? ParentId { get; set; }

    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; }

    /// <summary>Gesetzt, wenn der Ordner im Papierkorb liegt (selbst gelöscht oder mit einem Elternordner).</summary>
    public DateTime? DeletedAt { get; set; }

    public Guid? DeletedBy { get; set; }

    /// <summary>Element, das der Nutzer gelöscht hat; alle mitgelöschten Nachfahren tragen dieselbe ID.</summary>
    public Guid? DeletedRootId { get; set; }
}
