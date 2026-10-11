using DataspaceDrive.Core.Access;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Scanning;
using DataspaceDrive.Infrastructure.Storage;
using DataspaceDrive.Infrastructure.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Core.Tests.Files;

/// <summary>
/// Gleichzeitige Uploads mit demselben Namen: Ein Interceptor schiebt den konkurrierenden Abschluss genau zwischen
/// die Prüfung „Datei vorhanden?“ und das Speichern, so wie es zwei parallele Requests tun können.
/// </summary>
public sealed class ConcurrentUploadTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "drive-race-" + Guid.NewGuid().ToString("N"));
    private DrivePrincipal _principal = null!;
    private Guid _root;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = CreateDb();
        await db.Database.EnsureCreatedAsync();
        var user = await new UserProvisioning(db, TimeProvider.System).EnsureUserAsync("race-user", "Race", null, CancellationToken.None);
        _principal = new DrivePrincipal(user.Id, new HashSet<string> { DrivePrincipal.RoleUser }, new HashSet<string>());
        _root = (await db.Spaces.SingleAsync()).RootFolderId;
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        if (Directory.Exists(_dataRoot))
            Directory.Delete(_dataRoot, recursive: true);
    }

    [Fact]
    public async Task Simultaneous_upload_with_the_same_name_becomes_the_next_version()
    {
        await using var dbA = CreateDb();
        var uploadsA = CreateUploads(dbA);
        var first = await StartAsync(uploadsA, "Protokoll.txt", "erste");
        var race = new BeforeSave(() => uploadsA.CompleteAsync(_principal, first, "", CancellationToken.None));
        await using var dbB = CreateDb(race);
        var uploadsB = CreateUploads(dbB);
        var second = await StartAsync(uploadsB, "Protokoll.txt", "zweite");

        race.Armed = true;
        var item = await uploadsB.CompleteAsync(_principal, second, "", CancellationToken.None);

        Assert.Equal(2, item.VersionNo);
        await using var check = CreateDb();
        var file = await check.Files.SingleAsync();
        Assert.Equal(file.Id, item.Id);
        Assert.Equal([1, 2], await check.FileVersions.Where(v => v.FileEntryId == file.Id).OrderBy(v => v.VersionNo).Select(v => v.VersionNo).ToListAsync());
        Assert.Empty(await check.UploadSessions.ToListAsync());
    }

    [Fact]
    public async Task A_completion_that_cannot_be_saved_leaves_no_orphaned_blob()
    {
        var failing = new BeforeSave(() => throw new DbUpdateException("simulierter Konflikt"), always: true);
        await using var db = CreateDb(failing);
        var uploads = CreateUploads(db);
        var upload = await StartAsync(uploads, "Bericht.txt", "inhalt");

        failing.Armed = true;
        await Assert.ThrowsAsync<DbUpdateException>(() => uploads.CompleteAsync(_principal, upload, "", CancellationToken.None));

        var blobDirectory = Path.Combine(_dataRoot, "blobs");
        Assert.True(!Directory.Exists(blobDirectory) || !Directory.EnumerateFiles(blobDirectory, "*", SearchOption.AllDirectories).Any());
    }

    private DriveDbContext CreateDb(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<DriveDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    private UploadService CreateUploads(DriveDbContext db)
    {
        var blobs = new FileSystemBlobStore(Options.Create(new FileSystemBlobStoreOptions { RootPath = _dataRoot }));
        return new UploadService(db, new AccessResolver(db), new FileSystemUploadStaging(blobs), blobs, new DisabledVirusScanner(),
            TimeProvider.System, Options.Create(new UploadOptions()));
    }

    private async Task<Guid> StartAsync(UploadService uploads, string name, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var ticket = await uploads.StartAsync(_principal, _root, name, bytes.Length, CancellationToken.None);
        await uploads.AppendChunkAsync(_principal, ticket.UploadId, 0, new MemoryStream(bytes), CancellationToken.None);
        return ticket.UploadId;
    }

    /// <summary>Führt die Aktion vor dem ersten (oder jedem) Speichern aus, sobald scharf geschaltet.</summary>
    private sealed class BeforeSave(Func<Task> action, bool always = false) : SaveChangesInterceptor
    {
        private bool _fired;

        public bool Armed { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && (always || !_fired))
            {
                _fired = true;
                await action();
            }
            return result;
        }
    }
}
