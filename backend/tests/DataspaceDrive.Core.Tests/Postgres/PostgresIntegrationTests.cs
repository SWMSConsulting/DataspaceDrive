using DataspaceDrive.Core.Access;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Scanning;
using DataspaceDrive.Infrastructure.Storage;
using DataspaceDrive.Infrastructure.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace DataspaceDrive.Core.Tests.Postgres;

/// <summary>Läuft nur, wenn Docker verfügbar ist (in der CI immer); lokal ohne Docker wird er übersprungen.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public static bool Available =>
        File.Exists("/var/run/docker.sock") || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST"));

    public DockerFactAttribute()
    {
        if (!Available)
            Skip = "Docker ist nicht verfügbar; der PostgreSQL-Integrationstest läuft in der CI.";
    }
}

/// <summary>
/// Prüft, was SQLite in den API-Tests nicht abdeckt: die Npgsql-Migration, gefilterte eindeutige Indizes,
/// Fremdschlüssel und UTC-Zeitstempel auf echtem PostgreSQL.
/// </summary>
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "drive-pg-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        if (!DockerFactAttribute.Available)
            return;
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
        if (Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }

    [DockerFact]
    public async Task Migration_applies_and_the_file_flow_works_on_postgres()
    {
        var options = new DbContextOptionsBuilder<DriveDbContext>().UseNpgsql(_postgres!.GetConnectionString()).Options;
        await using var db = new DriveDbContext(options);
        await db.Database.MigrateAsync();

        var clock = TimeProvider.System;
        var user = await new UserProvisioning(db, clock).EnsureUserAsync("pg-user", "PG", null, CancellationToken.None);
        var principal = new DrivePrincipal(user.Id, new HashSet<string> { DrivePrincipal.RoleUser }, new HashSet<string>());
        var blobs = new FileSystemBlobStore(Options.Create(new FileSystemBlobStoreOptions { RootPath = _dataRoot }));
        var access = new AccessResolver(db);
        var files = new FileService(db, access, blobs, clock, NullLogger<FileService>.Instance);
        var uploads = new UploadService(db, access, new FileSystemUploadStaging(blobs), blobs, new DisabledVirusScanner(), clock,
            Options.Create(new UploadOptions { ChunkSize = 4 }));

        var root = (await files.GetSpacesAsync(principal, CancellationToken.None)).Single().RootFolderId;
        var folder = await files.CreateFolderAsync(principal, root, "Berichte", CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => files.CreateFolderAsync(principal, root, "BERICHTE", CancellationToken.None));

        var ticket = await uploads.StartAsync(principal, folder.Id, "Protokoll.txt", 6, CancellationToken.None);
        await uploads.AppendChunkAsync(principal, ticket.UploadId, 0, new MemoryStream("abcd"u8.ToArray()), CancellationToken.None);
        await uploads.AppendChunkAsync(principal, ticket.UploadId, 1, new MemoryStream("ef"u8.ToArray()), CancellationToken.None);
        var item = await uploads.CompleteAsync(principal, ticket.UploadId, "", CancellationToken.None);
        Assert.Equal("text/plain", item.ContentType);
        Assert.Single(await files.SearchAsync(principal, "protokoll", CancellationToken.None));

        await files.DeleteAsync(principal, folder.Id, CancellationToken.None);
        Assert.Single(await files.GetTrashAsync(principal, CancellationToken.None));
        await files.PurgeAsync(principal, folder.Id, CancellationToken.None);
        Assert.Empty(await files.GetTrashAsync(principal, CancellationToken.None));
        Assert.Empty(await files.SearchAsync(principal, "protokoll", CancellationToken.None));

        // Team-Bereich: Belegung über GroupBy/Join muss auch Npgsql übersetzen.
        var admin = principal with { Roles = new HashSet<string> { DrivePrincipal.RoleUser, DrivePrincipal.RoleAdmin } };
        var teams = new TeamSpaceService(db, clock);
        var team = await teams.CreateAsync(admin,
            new TeamSpaceRequest("Projekt", [new AclEntryRequest(PrincipalType.User, user.Id.ToString(), Permission.Write)]), CancellationToken.None);
        var teamUpload = await uploads.StartAsync(principal, team.RootFolderId, "Plan.txt", 3, CancellationToken.None);
        await uploads.AppendChunkAsync(principal, teamUpload.UploadId, 0, new MemoryStream("abc"u8.ToArray()), CancellationToken.None);
        await uploads.CompleteAsync(principal, teamUpload.UploadId, "", CancellationToken.None);
        var listed = Assert.Single(await teams.ListAsync(admin, CancellationToken.None));
        Assert.Equal((1, 3L), (listed.FileCount, listed.StoredBytes));
    }
}
