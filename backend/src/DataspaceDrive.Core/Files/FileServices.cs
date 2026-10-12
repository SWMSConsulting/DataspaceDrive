using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Users;

namespace DataspaceDrive.Core.Files;

/// <summary><paramref name="ExternalSharingAllowed"/>: aus diesem Bereich darf an Partner freigegeben werden.</summary>
public sealed record SpaceInfo(Guid Id, string Name, SpaceKind Kind, Guid RootFolderId, Permission Permission, bool ExternalSharingAllowed);

public sealed record PathSegment(Guid Id, string Name);

public sealed record DriveItem(
    Guid Id,
    bool IsDirectory,
    string Name,
    Guid SpaceId,
    Guid? ParentId,
    long Size,
    DateTime ModifiedAt,
    ScanStatus? ScanStatus,
    int? VersionNo,
    string? ContentType);

public sealed record FolderListing(Guid FolderId, Guid SpaceId, Permission Permission, IReadOnlyList<PathSegment> Path,
    IReadOnlyList<DriveItem> Items);

public sealed record TrashItem(Guid Id, bool IsDirectory, string Name, Guid SpaceId, string OriginalPath, DateTime DeletedAt);

public sealed record FileVersionInfo(int VersionNo, long Size, string Sha256, ScanStatus ScanStatus, DateTime UploadedAt);

public sealed record FileContent(string FileName, string ContentType, long Size, string BlobKey);

public sealed record UploadTicket(Guid UploadId, int ChunkSize);

public interface IFileService
{
    Task<IReadOnlyList<SpaceInfo>> GetSpacesAsync(DrivePrincipal principal, CancellationToken ct);
    Task<FolderListing> GetChildrenAsync(DrivePrincipal principal, Guid folderId, CancellationToken ct);
    Task<DriveItem> CreateFolderAsync(DrivePrincipal principal, Guid parentId, string name, CancellationToken ct);
    Task<DriveItem> RenameAsync(DrivePrincipal principal, Guid itemId, string newName, CancellationToken ct);
    Task<DriveItem> MoveAsync(DrivePrincipal principal, Guid itemId, Guid targetFolderId, CancellationToken ct);
    Task DeleteAsync(DrivePrincipal principal, Guid itemId, CancellationToken ct);
    Task<IReadOnlyList<TrashItem>> GetTrashAsync(DrivePrincipal principal, CancellationToken ct);
    Task<DriveItem> RestoreAsync(DrivePrincipal principal, Guid itemId, CancellationToken ct);
    Task PurgeAsync(DrivePrincipal principal, Guid itemId, CancellationToken ct);
    Task<IReadOnlyList<DriveItem>> SearchAsync(DrivePrincipal principal, string query, CancellationToken ct);
    Task<IReadOnlyList<FileVersionInfo>> GetVersionsAsync(DrivePrincipal principal, Guid fileId, CancellationToken ct);
    Task<FileContent> GetContentAsync(DrivePrincipal principal, Guid fileId, int? versionNo, CancellationToken ct);
}

public interface IUploadService
{
    /// <summary>Größte annehmbare Datei; mit Virenscan zusätzlich durch die Grenze des Scanners beschränkt.</summary>
    long MaxFileSize { get; }

    Task<UploadTicket> StartAsync(DrivePrincipal principal, Guid folderId, string fileName, long size, CancellationToken ct);

    /// <summary>Nimmt Teilstück <paramref name="index"/> an; wiederholte Teilstücke werden ignoriert.</summary>
    Task<long> AppendChunkAsync(DrivePrincipal principal, Guid uploadId, int index, Stream chunk, CancellationToken ct);

    /// <summary>Schließt den Upload ab; existiert im Ordner bereits eine Datei gleichen Namens, entsteht eine neue Version.</summary>
    Task<DriveItem> CompleteAsync(DrivePrincipal principal, Guid uploadId, string contentType, CancellationToken ct);

    Task AbortAsync(DrivePrincipal principal, Guid uploadId, CancellationToken ct);
}

public interface IUserProvisioning
{
    /// <summary>Legt den Nutzer beim ersten Login samt persönlichem Bereich an und aktualisiert Name und E-Mail.</summary>
    Task<DriveUser> EnsureUserAsync(string subject, string displayName, string? email, CancellationToken ct);

    /// <summary>Merkt sich die Rollen aus der Anmeldung, wenn sie sich geändert haben.</summary>
    Task RecordRolesAsync(DriveUser user, IReadOnlyCollection<string> roles, CancellationToken ct);

    Task SetEmailNotificationsAsync(DriveUser user, bool enabled, CancellationToken ct);
}
