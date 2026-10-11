namespace DataspaceDrive.Core.Files;

/// <summary>Laufender Upload in Teilstücken; die Teile landen in der Upload-Ablage, bis er abgeschlossen ist.</summary>
public sealed class UploadSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FolderId { get; set; }
    public required string FileName { get; set; }
    public long DeclaredSize { get; set; }
    public int ChunkSize { get; set; }
    public long ReceivedBytes { get; set; }
    public int NextChunkIndex { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
