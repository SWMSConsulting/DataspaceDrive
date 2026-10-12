using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataspaceDrive.Infrastructure.Files;

public sealed class FileService(
    DriveDbContext db,
    AccessResolver access,
    IBlobStore blobs,
    TimeProvider clock,
    ILogger<FileService> logger) : IFileService
{
    private const int MaxPathDepth = 64;
    private const int SearchLimit = 100;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<SpaceInfo>> GetSpacesAsync(DrivePrincipal principal, CancellationToken ct)
    {
        var spaces = await access.AccessibleSpacesAsync(principal, Permission.Read, ct);
        return spaces.Select(s => new SpaceInfo(s.Space.Id, s.Space.Name, s.Space.Kind, s.Space.RootFolderId, s.Permission,
            s.Space.Kind == SpaceKind.Personal || s.Space.ExternalSharingAllowed)).ToList();
    }

    public async Task<FolderListing> GetChildrenAsync(DrivePrincipal principal, Guid folderId, CancellationToken ct)
    {
        var folder = await ActiveFolderAsync(folderId, ct);
        var space = await access.RequireAsync(principal, folder.SpaceId, Permission.Read, ct);
        var permission = await access.PermissionAsync(principal, space, ct);

        var folders = await db.Folders
            .Where(f => f.ParentId == folderId && f.DeletedAt == null)
            .OrderBy(f => f.Name)
            .ToListAsync(ct);
        var files = await (from f in db.Files
                           where f.FolderId == folderId && f.DeletedAt == null
                           join v in db.FileVersions on f.CurrentVersionId equals v.Id into versions
                           from v in versions.DefaultIfEmpty()
                           orderby f.Name
                           select new { File = f, Version = v })
            .ToListAsync(ct);

        var items = folders.Select(ToItem)
            .Concat(files.Select(x => ToItem(x.File, x.Version)))
            .ToList();
        return new FolderListing(folder.Id, folder.SpaceId, permission, await PathAsync(folder, ct), items);
    }

    public async Task<DriveItem> CreateFolderAsync(DrivePrincipal principal, Guid parentId, string name, CancellationToken ct)
    {
        var parent = await ActiveFolderAsync(parentId, ct);
        await access.RequireAsync(principal, parent.SpaceId, Permission.Write, ct);
        var valid = ItemName.Validate(name);
        await EnsureNameFreeAsync(parent.Id, ItemName.Normalize(valid), null, ct);

        var folder = new Folder
        {
            Id = Guid.NewGuid(), SpaceId = parent.SpaceId, ParentId = parent.Id, Name = valid, NormalizedName = ItemName.Normalize(valid),
            CreatedAt = Now, CreatedBy = principal.UserId, ModifiedAt = Now,
        };
        db.Folders.Add(folder);
        Audit(principal, "folder.created", "folder", folder.Id, valid);
        await SaveUniqueAsync(ct);
        return ToItem(folder);
    }

    public async Task<DriveItem> RenameAsync(DrivePrincipal principal, Guid itemId, string newName, CancellationToken ct)
    {
        var valid = ItemName.Validate(newName);
        var normalized = ItemName.Normalize(valid);
        var (folder, file) = await ActiveItemAsync(itemId, ct);
        if (folder is not null)
        {
            if (folder.ParentId is null)
                throw new InvalidInputException("Der Wurzelordner eines Bereichs kann nicht umbenannt werden.");
            await access.RequireAsync(principal, folder.SpaceId, Permission.Write, ct);
            await EnsureNameFreeAsync(folder.ParentId.Value, normalized, folder.Id, ct);
            Audit(principal, "folder.renamed", "folder", folder.Id, $"{folder.Name} -> {valid}");
            folder.Name = valid;
            folder.NormalizedName = normalized;
            folder.ModifiedAt = Now;
            await SaveUniqueAsync(ct);
            return ToItem(folder);
        }

        await access.RequireAsync(principal, file!.SpaceId, Permission.Write, ct);
        await EnsureNameFreeAsync(file.FolderId, normalized, file.Id, ct);
        Audit(principal, "file.renamed", "file", file.Id, $"{file.Name} -> {valid}");
        file.Name = valid;
        file.NormalizedName = normalized;
        file.ModifiedAt = Now;
        file.ConcurrencyStamp = Guid.NewGuid();
        await SaveUniqueAsync(ct);
        return ToItem(file, await CurrentVersionAsync(file, ct));
    }

    public async Task<DriveItem> MoveAsync(DrivePrincipal principal, Guid itemId, Guid targetFolderId, CancellationToken ct)
    {
        var target = await ActiveFolderAsync(targetFolderId, ct);
        var (folder, file) = await ActiveItemAsync(itemId, ct);
        var spaceId = folder?.SpaceId ?? file!.SpaceId;
        if (target.SpaceId != spaceId)
            throw new InvalidInputException("Verschieben zwischen Bereichen ist noch nicht möglich.");
        await access.RequireAsync(principal, spaceId, Permission.Write, ct);

        if (folder is not null)
        {
            if (folder.ParentId is null)
                throw new InvalidInputException("Der Wurzelordner eines Bereichs kann nicht verschoben werden.");
            if (await IsSelfOrDescendantAsync(target, folder.Id, ct))
                throw new InvalidInputException("Ein Ordner kann nicht in sich selbst verschoben werden.");
            await EnsureNameFreeAsync(target.Id, folder.NormalizedName, folder.Id, ct);
            folder.ParentId = target.Id;
            folder.ModifiedAt = Now;
            Audit(principal, "folder.moved", "folder", folder.Id, target.Id.ToString());
            await SaveUniqueAsync(ct);
            return ToItem(folder);
        }

        await EnsureNameFreeAsync(target.Id, file!.NormalizedName, file.Id, ct);
        file.FolderId = target.Id;
        file.ModifiedAt = Now;
        file.ConcurrencyStamp = Guid.NewGuid();
        Audit(principal, "file.moved", "file", file.Id, target.Id.ToString());
        await SaveUniqueAsync(ct);
        return ToItem(file, await CurrentVersionAsync(file, ct));
    }

    public async Task DeleteAsync(DrivePrincipal principal, Guid itemId, CancellationToken ct)
    {
        var (folder, file) = await ActiveItemAsync(itemId, ct);
        var now = Now;
        if (file is not null)
        {
            await access.RequireAsync(principal, file.SpaceId, Permission.Write, ct);
            MarkDeleted(file, principal.UserId, file.Id, now);
            Audit(principal, "file.deleted", "file", file.Id, file.Name);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (folder!.ParentId is null)
            throw new InvalidInputException("Der Wurzelordner eines Bereichs kann nicht gelöscht werden.");
        await access.RequireAsync(principal, folder.SpaceId, Permission.Write, ct);
        // Nachfahren tragen die ID des gelöschten Ordners, damit Wiederherstellen und endgültiges Löschen sie finden.
        var folderIds = await ActiveDescendantFolderIdsAsync(folder.Id, ct);
        var folders = await db.Folders.Where(f => folderIds.Contains(f.Id)).ToListAsync(ct);
        var files = await db.Files.Where(f => folderIds.Contains(f.FolderId) && f.DeletedAt == null).ToListAsync(ct);
        foreach (var f in folders)
        {
            f.DeletedAt = now;
            f.DeletedBy = principal.UserId;
            f.DeletedRootId = folder.Id;
        }
        foreach (var f in files)
            MarkDeleted(f, principal.UserId, folder.Id, now);
        Audit(principal, "folder.deleted", "folder", folder.Id, $"{folder.Name} ({folders.Count - 1} Unterordner, {files.Count} Dateien)");
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TrashItem>> GetTrashAsync(DrivePrincipal principal, CancellationToken ct)
    {
        var spaceIds = (await access.AccessibleSpacesAsync(principal, Permission.Write, ct)).Select(s => s.Space.Id).ToList();
        var folders = await db.Folders
            .Where(f => spaceIds.Contains(f.SpaceId) && f.DeletedRootId == f.Id)
            .ToListAsync(ct);
        var files = await db.Files
            .Where(f => spaceIds.Contains(f.SpaceId) && f.DeletedRootId == f.Id)
            .ToListAsync(ct);

        var result = new List<TrashItem>();
        foreach (var f in folders)
            result.Add(new TrashItem(f.Id, true, f.Name, f.SpaceId, await PathTextAsync(f.ParentId, ct), f.DeletedAt!.Value));
        foreach (var f in files)
            result.Add(new TrashItem(f.Id, false, f.Name, f.SpaceId, await PathTextAsync(f.FolderId, ct), f.DeletedAt!.Value));
        return result.OrderByDescending(t => t.DeletedAt).ToList();
    }

    public async Task<DriveItem> RestoreAsync(DrivePrincipal principal, Guid itemId, CancellationToken ct)
    {
        var (folder, file) = await TrashRootAsync(itemId, ct);
        var spaceId = folder?.SpaceId ?? file!.SpaceId;
        var space = await access.RequireAsync(principal, spaceId, Permission.Write, ct);

        // Liegt der frühere Elternordner selbst im Papierkorb, kommt das Element in den Wurzelordner des Bereichs.
        var parentId = folder?.ParentId ?? file!.FolderId;
        var parent = await db.Folders.FirstOrDefaultAsync(f => f.Id == parentId, ct);
        var targetId = parent is { DeletedAt: null } ? parent.Id : space.RootFolderId;

        var name = folder?.Name ?? file!.Name;
        var freeName = await FreeNameAsync(targetId, name, ct);

        var folders = await db.Folders.Where(f => f.DeletedRootId == itemId).ToListAsync(ct);
        var files = await db.Files.Where(f => f.DeletedRootId == itemId).ToListAsync(ct);
        foreach (var f in folders)
        {
            f.DeletedAt = null;
            f.DeletedBy = null;
            f.DeletedRootId = null;
        }
        foreach (var f in files)
        {
            f.DeletedAt = null;
            f.DeletedBy = null;
            f.DeletedRootId = null;
            f.ConcurrencyStamp = Guid.NewGuid();
        }

        if (folder is not null)
        {
            folder.ParentId = targetId;
            folder.Name = freeName;
            folder.NormalizedName = ItemName.Normalize(freeName);
            folder.ModifiedAt = Now;
        }
        else
        {
            file!.FolderId = targetId;
            file.Name = freeName;
            file.NormalizedName = ItemName.Normalize(freeName);
            file.ModifiedAt = Now;
        }
        Audit(principal, folder is not null ? "folder.restored" : "file.restored", folder is not null ? "folder" : "file", itemId, freeName);
        await SaveUniqueAsync(ct);
        return folder is not null ? ToItem(folder) : ToItem(file!, await CurrentVersionAsync(file!, ct));
    }

    public async Task PurgeAsync(DrivePrincipal principal, Guid itemId, CancellationToken ct)
    {
        var (folder, file) = await TrashRootAsync(itemId, ct);
        await access.RequireAsync(principal, folder?.SpaceId ?? file!.SpaceId, Permission.Write, ct);

        var folders = await db.Folders.Where(f => f.DeletedRootId == itemId).ToListAsync(ct);
        var files = await db.Files.Where(f => f.DeletedRootId == itemId).ToListAsync(ct);
        var fileIds = files.Select(f => f.Id).ToList();
        var versions = await db.FileVersions.Where(v => fileIds.Contains(v.FileEntryId)).ToListAsync(ct);
        var versionIds = versions.Select(v => v.Id).ToList();
        var shared = await (from i in db.ShareItems
                            join d in db.Deliveries on i.ShareId equals d.ShareId
                            where i.FileVersionId != null && versionIds.Contains(i.FileVersionId.Value)
                                  && d.State != DeliveryState.Revoked && d.State != DeliveryState.Expired && d.State != DeliveryState.Rejected
                            select i.Name).FirstOrDefaultAsync(ct);
        if (shared is not null)
            throw new ConflictException($"„{shared}“ ist noch an Partner freigegeben; bitte zuerst die Freigabe widerrufen.");

        db.FileVersions.RemoveRange(versions);
        db.Files.RemoveRange(files);
        db.Folders.RemoveRange(folders);
        Audit(principal, "item.purged", folder is not null ? "folder" : "file", itemId,
            $"{folder?.Name ?? file!.Name} ({files.Count} Dateien, {versions.Count} Versionen)");
        await db.SaveChangesAsync(ct);

        // Blobs erst nach dem Commit löschen; ein verwaister Blob ist harmlos, ein fehlender wäre Datenverlust.
        foreach (var version in versions)
        {
            try
            {
                await blobs.DeleteAsync(version.BlobKey, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Blob {BlobKey} konnte nicht gelöscht werden", version.BlobKey);
            }
        }
    }

    public async Task<IReadOnlyList<DriveItem>> SearchAsync(DrivePrincipal principal, string query, CancellationToken ct)
    {
        var term = (query ?? "").Trim();
        if (term.Length < 2)
            throw new InvalidInputException("Der Suchbegriff muss mindestens zwei Zeichen haben.");
        var normalized = ItemName.Normalize(term);
        var spaceIds = (await access.AccessibleSpacesAsync(principal, Permission.Read, ct)).Select(s => s.Space.Id).ToList();

        var folders = await db.Folders
            .Where(f => spaceIds.Contains(f.SpaceId) && f.DeletedAt == null && f.ParentId != null && f.NormalizedName.Contains(normalized))
            .OrderBy(f => f.Name)
            .Take(SearchLimit)
            .ToListAsync(ct);
        var files = await (from f in db.Files
                           where spaceIds.Contains(f.SpaceId) && f.DeletedAt == null && f.NormalizedName.Contains(normalized)
                           join v in db.FileVersions on f.CurrentVersionId equals v.Id into versions
                           from v in versions.DefaultIfEmpty()
                           orderby f.Name
                           select new { File = f, Version = v })
            .Take(SearchLimit)
            .ToListAsync(ct);
        return folders.Select(ToItem).Concat(files.Select(x => ToItem(x.File, x.Version))).ToList();
    }

    public async Task<IReadOnlyList<FileVersionInfo>> GetVersionsAsync(DrivePrincipal principal, Guid fileId, CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId && f.DeletedAt == null, ct)
            ?? throw new NotFoundException("Die Datei wurde nicht gefunden.");
        await access.RequireAsync(principal, file.SpaceId, Permission.Read, ct);
        return await db.FileVersions
            .Where(v => v.FileEntryId == fileId)
            .OrderByDescending(v => v.VersionNo)
            .Select(v => new FileVersionInfo(v.VersionNo, v.SizeBytes, v.Sha256, v.ScanStatus, v.UploadedAt))
            .ToListAsync(ct);
    }

    public async Task<FileContent> GetContentAsync(DrivePrincipal principal, Guid fileId, int? versionNo, CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId && f.DeletedAt == null, ct)
            ?? throw new NotFoundException("Die Datei wurde nicht gefunden.");
        await access.RequireAsync(principal, file.SpaceId, Permission.Read, ct);
        var version = versionNo is null
            ? await CurrentVersionAsync(file, ct)
            : await db.FileVersions.FirstOrDefaultAsync(v => v.FileEntryId == fileId && v.VersionNo == versionNo, ct);
        if (version is null)
            throw new NotFoundException("Die Version wurde nicht gefunden.");

        return version.ScanStatus switch
        {
            ScanStatus.Clean or ScanStatus.NotScanned => new FileContent(file.Name, version.ContentType, version.SizeBytes, version.BlobKey),
            ScanStatus.Pending => throw new ConflictException("Die Datei wird noch auf Viren geprüft."),
            ScanStatus.Infected => throw new AccessDeniedException($"Die Datei ist gesperrt: Virenfund ({version.ScanDetail})."),
            _ => throw new AccessDeniedException("Die Datei ist gesperrt, weil sie nicht auf Viren geprüft werden konnte."),
        };
    }

    private void Audit(DrivePrincipal principal, string action, string targetType, Guid targetId, string? details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = Now, ActorUserId = principal.UserId, Action = action, TargetType = targetType,
            TargetId = targetId.ToString(), Details = details,
        });

    private static void MarkDeleted(FileEntry file, Guid userId, Guid rootId, DateTime now)
    {
        file.DeletedAt = now;
        file.DeletedBy = userId;
        file.DeletedRootId = rootId;
        file.ConcurrencyStamp = Guid.NewGuid();
    }

    private async Task<Folder> ActiveFolderAsync(Guid folderId, CancellationToken ct) =>
        await db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.DeletedAt == null, ct)
        ?? throw new NotFoundException("Der Ordner wurde nicht gefunden.");

    private async Task<(Folder? Folder, FileEntry? File)> ActiveItemAsync(Guid itemId, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == itemId && f.DeletedAt == null, ct);
        if (folder is not null)
            return (folder, null);
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == itemId && f.DeletedAt == null, ct);
        return file is not null ? (null, file) : throw new NotFoundException("Das Element wurde nicht gefunden.");
    }

    private async Task<(Folder? Folder, FileEntry? File)> TrashRootAsync(Guid itemId, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == itemId && f.DeletedRootId == itemId, ct);
        if (folder is not null)
            return (folder, null);
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == itemId && f.DeletedRootId == itemId, ct);
        return file is not null ? (null, file) : throw new NotFoundException("Das Element liegt nicht im Papierkorb.");
    }

    private async Task<FileVersion?> CurrentVersionAsync(FileEntry file, CancellationToken ct) =>
        file.CurrentVersionId is null ? null : await db.FileVersions.FirstOrDefaultAsync(v => v.Id == file.CurrentVersionId, ct);

    /// <summary>Namen sind je Ordner über Dateien und Unterordner hinweg eindeutig.</summary>
    internal async Task EnsureNameFreeAsync(Guid folderId, string normalizedName, Guid? exceptId, CancellationToken ct)
    {
        var taken = await db.Folders.AnyAsync(f => f.ParentId == folderId && f.DeletedAt == null && f.NormalizedName == normalizedName
                                                   && f.Id != exceptId, ct)
                    || await db.Files.AnyAsync(f => f.FolderId == folderId && f.DeletedAt == null && f.NormalizedName == normalizedName
                                                    && f.Id != exceptId, ct);
        if (taken)
            throw new ConflictException("In diesem Ordner gibt es bereits ein Element mit diesem Namen.");
    }

    private async Task<string> FreeNameAsync(Guid folderId, string name, CancellationToken ct)
    {
        for (var counter = 1; counter < 1000; counter++)
        {
            var candidate = counter == 1 ? name : ItemName.WithCounter(name, counter);
            var normalized = ItemName.Normalize(candidate);
            var taken = await db.Folders.AnyAsync(f => f.ParentId == folderId && f.DeletedAt == null && f.NormalizedName == normalized, ct)
                        || await db.Files.AnyAsync(f => f.FolderId == folderId && f.DeletedAt == null && f.NormalizedName == normalized, ct);
            if (!taken)
                return candidate;
        }
        throw new ConflictException("Es konnte kein freier Name gefunden werden.");
    }

    private async Task<List<Guid>> ActiveDescendantFolderIdsAsync(Guid folderId, CancellationToken ct)
    {
        var all = new List<Guid> { folderId };
        var frontier = new List<Guid> { folderId };
        while (frontier.Count > 0)
        {
            var current = frontier;
            frontier = await db.Folders
                .Where(f => f.ParentId != null && current.Contains(f.ParentId.Value) && f.DeletedAt == null)
                .Select(f => f.Id)
                .ToListAsync(ct);
            all.AddRange(frontier);
        }
        return all;
    }

    private async Task<bool> IsSelfOrDescendantAsync(Folder candidate, Guid ancestorId, CancellationToken ct)
    {
        var current = candidate;
        for (var depth = 0; depth < MaxPathDepth && current is not null; depth++)
        {
            if (current.Id == ancestorId)
                return true;
            if (current.ParentId is null)
                return false;
            var parentId = current.ParentId.Value;
            current = await db.Folders.FirstOrDefaultAsync(f => f.Id == parentId, ct);
        }
        return false;
    }

    private async Task<IReadOnlyList<PathSegment>> PathAsync(Folder folder, CancellationToken ct)
    {
        var segments = new List<PathSegment>();
        Folder? current = folder;
        for (var depth = 0; depth < MaxPathDepth && current is not null; depth++)
        {
            segments.Add(new PathSegment(current.Id, current.Name));
            if (current.ParentId is null)
                break;
            var parentId = current.ParentId.Value;
            current = await db.Folders.FirstOrDefaultAsync(f => f.Id == parentId, ct);
        }
        segments.Reverse();
        return segments;
    }

    private async Task<string> PathTextAsync(Guid? folderId, CancellationToken ct)
    {
        if (folderId is null)
            return "/";
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == folderId, ct);
        return folder is null ? "/" : "/" + string.Join('/', (await PathAsync(folder, ct)).Select(s => s.Name));
    }

    private async Task SaveUniqueAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            // Der eindeutige Index hat einen gleichzeitigen Konflikt erkannt, den die Vorabprüfung nicht sehen konnte.
            throw new ConflictException("In diesem Ordner gibt es bereits ein Element mit diesem Namen.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("Das Element wurde zwischenzeitlich geändert. Bitte neu laden.");
        }
    }

    internal static DriveItem ToItem(Folder folder) =>
        new(folder.Id, true, folder.Name, folder.SpaceId, folder.ParentId, 0, folder.ModifiedAt, null, null, null);

    internal static DriveItem ToItem(FileEntry file, FileVersion? version) =>
        new(file.Id, false, file.Name, file.SpaceId, file.FolderId, version?.SizeBytes ?? 0, file.ModifiedAt, version?.ScanStatus,
            version?.VersionNo, version?.ContentType);
}
