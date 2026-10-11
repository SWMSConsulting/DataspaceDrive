using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Scanning;
using DataspaceDrive.Core.Storage;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Infrastructure.Files;

public sealed class UploadOptions
{
    /// <summary>Größe der Teilstücke; der Ingress muss Requests dieser Größe durchlassen.</summary>
    public int ChunkSize { get; set; } = 8 * 1024 * 1024;

    public long MaxFileSize { get; set; } = 10L * 1024 * 1024 * 1024;

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);
}

public sealed class UploadService(
    DriveDbContext db,
    AccessResolver access,
    IUploadStaging staging,
    IVirusScanner scanner,
    TimeProvider clock,
    IOptions<UploadOptions> options) : IUploadService
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly UploadOptions _options = options.Value;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<UploadTicket> StartAsync(DrivePrincipal principal, Guid folderId, string fileName, long size, CancellationToken ct)
    {
        var name = ItemName.Validate(fileName);
        if (size < 0 || size > _options.MaxFileSize)
            throw new InvalidInputException($"Dateien dürfen höchstens {_options.MaxFileSize / (1024 * 1024)} MB groß sein.");
        // Ungeprüfte Dateien bleiben gesperrt; ein Upload, der nie herunterladbar wäre, wird gleich abgelehnt.
        if (scanner.Enabled && size > scanner.MaxBytes)
            throw new InvalidInputException(
                $"Dateien über {scanner.MaxBytes / (1024 * 1024)} MB kann der Virenscan nicht prüfen; sie werden deshalb nicht angenommen.");
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.DeletedAt == null, ct)
            ?? throw new NotFoundException("Der Ordner wurde nicht gefunden.");
        await access.RequireAsync(principal, folder.SpaceId, Permission.Write, ct);
        if (await db.Folders.AnyAsync(f => f.ParentId == folderId && f.DeletedAt == null && f.NormalizedName == ItemName.Normalize(name), ct))
            throw new ConflictException("In diesem Ordner gibt es bereits einen Ordner mit diesem Namen.");

        var session = new UploadSession
        {
            Id = Guid.NewGuid(), UserId = principal.UserId, FolderId = folderId, FileName = name, DeclaredSize = size,
            ChunkSize = _options.ChunkSize, CreatedAt = Now, ExpiresAt = Now + _options.SessionLifetime,
        };
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new UploadTicket(session.Id, session.ChunkSize);
    }

    public async Task<long> AppendChunkAsync(DrivePrincipal principal, Guid uploadId, int index, Stream chunk, CancellationToken ct)
    {
        var session = await OwnSessionAsync(principal, uploadId, ct);
        if (index < session.NextChunkIndex)
            return session.ReceivedBytes; // Wiederholung eines bereits angenommenen Teilstücks
        if (index > session.NextChunkIndex)
            throw new ConflictException($"Teilstück {session.NextChunkIndex} wird erwartet, nicht {index}.");

        var remaining = session.DeclaredSize - session.ReceivedBytes;
        var written = await staging.WriteChunkAsync(session.Id, session.ReceivedBytes, chunk, Math.Min(session.ChunkSize, remaining), ct);
        session.ReceivedBytes += written;
        session.NextChunkIndex++;
        await db.SaveChangesAsync(ct);
        return session.ReceivedBytes;
    }

    public async Task<DriveItem> CompleteAsync(DrivePrincipal principal, Guid uploadId, string contentType, CancellationToken ct)
    {
        var session = await OwnSessionAsync(principal, uploadId, ct);
        if (session.ReceivedBytes != session.DeclaredSize)
            throw new ConflictException($"Upload unvollständig: {session.ReceivedBytes} von {session.DeclaredSize} Bytes empfangen.");
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == session.FolderId && f.DeletedAt == null, ct)
            ?? throw new NotFoundException("Der Zielordner existiert nicht mehr.");
        await access.RequireAsync(principal, folder.SpaceId, Permission.Write, ct);

        var normalized = ItemName.Normalize(session.FileName);
        if (await db.Folders.AnyAsync(f => f.ParentId == folder.Id && f.DeletedAt == null && f.NormalizedName == normalized, ct))
            throw new ConflictException("In diesem Ordner gibt es bereits einen Ordner mit diesem Namen.");

        var now = Now;
        var file = await db.Files.FirstOrDefaultAsync(f => f.FolderId == folder.Id && f.DeletedAt == null && f.NormalizedName == normalized, ct);
        var isNewFile = file is null;
        file ??= new FileEntry
        {
            Id = Guid.NewGuid(), SpaceId = folder.SpaceId, FolderId = folder.Id, Name = session.FileName, NormalizedName = normalized,
            CreatedAt = now, CreatedBy = principal.UserId,
        };
        var latest = isNewFile ? null : await db.FileVersions.Where(v => v.FileEntryId == file.Id).MaxAsync(v => (int?)v.VersionNo, ct);
        var versionNo = (latest ?? 0) + 1;

        var versionId = Guid.NewGuid();
        var blob = await staging.PromoteAsync(session.Id, $"blobs/{versionId:N}", ct);
        db.FileVersions.Add(new FileVersion
        {
            Id = versionId, FileEntryId = file.Id, VersionNo = versionNo, BlobKey = blob.Key, SizeBytes = blob.Length, Sha256 = blob.Sha256,
            ContentType = ResolveContentType(contentType, file.Name),
            ScanStatus = scanner.Enabled ? ScanStatus.Pending : ScanStatus.NotScanned, UploadedBy = principal.UserId, UploadedAt = now,
        });
        file.CurrentVersionId = versionId;
        file.ModifiedAt = now;
        file.ConcurrencyStamp = Guid.NewGuid();
        if (isNewFile)
            db.Files.Add(file);
        db.UploadSessions.Remove(session);
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = now, ActorUserId = principal.UserId, Action = isNewFile ? "file.uploaded" : "file.versioned", TargetType = "file",
            TargetId = file.Id.ToString(), Details = $"{file.Name} v{versionNo}, {blob.Length} Bytes, SHA-256 {blob.Sha256}",
        });
        await db.SaveChangesAsync(ct);

        var version = await db.FileVersions.FirstAsync(v => v.Id == versionId, ct);
        return FileService.ToItem(file, version);
    }

    public async Task AbortAsync(DrivePrincipal principal, Guid uploadId, CancellationToken ct)
    {
        var session = await OwnSessionAsync(principal, uploadId, ct);
        await staging.DiscardAsync(session.Id, ct);
        db.UploadSessions.Remove(session);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Angabe des Browsers, sonst aus der Dateiendung; der Download liefert ohnehin nur als Anhang aus.</summary>
    private static string ResolveContentType(string contentType, string fileName)
    {
        const string fallback = "application/octet-stream";
        if (!string.IsNullOrWhiteSpace(contentType) && contentType != fallback && contentType.Length <= 255)
            return contentType;
        return ContentTypes.TryGetContentType(fileName, out var guessed) ? guessed : fallback;
    }

    private async Task<UploadSession> OwnSessionAsync(DrivePrincipal principal, Guid uploadId, CancellationToken ct)
    {
        var session = await db.UploadSessions.FirstOrDefaultAsync(s => s.Id == uploadId && s.UserId == principal.UserId, ct)
            ?? throw new NotFoundException("Der Upload wurde nicht gefunden.");
        if (session.ExpiresAt < Now)
            throw new ConflictException("Der Upload ist abgelaufen. Bitte neu starten.");
        return session;
    }
}
