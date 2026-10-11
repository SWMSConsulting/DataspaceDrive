using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Users;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DataspaceDrive.Infrastructure.Persistence;

public sealed class DriveDbContext(DbContextOptions<DriveDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DriveUser> Users => Set<DriveUser>();
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<FileEntry> Files => Set<FileEntry>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<AclEntry> AclEntries => Set<AclEntry>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DriveUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Subject).IsUnique();
            e.Property(u => u.Subject).HasMaxLength(255);
            e.Property(u => u.Email).HasMaxLength(320);
            e.Property(u => u.DisplayName).HasMaxLength(255);
        });

        model.Entity<Space>(e =>
        {
            e.ToTable("spaces");
            e.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Name).HasMaxLength(ItemName.MaxLength);
            e.HasIndex(s => s.OwnerUserId);
            e.HasOne<DriveUser>().WithMany().HasForeignKey(s => s.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Folder>(e =>
        {
            e.ToTable("folders");
            e.Property(f => f.Name).HasMaxLength(ItemName.MaxLength);
            e.Property(f => f.NormalizedName).HasMaxLength(ItemName.MaxLength);
            // Eindeutige Namen je Elternordner, nur für nicht gelöschte Einträge (Wurzelordner haben keinen Elternordner).
            e.HasIndex(f => new { f.ParentId, f.NormalizedName }).IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL AND \"ParentId\" IS NOT NULL");
            e.HasIndex(f => f.SpaceId);
            e.HasIndex(f => f.DeletedRootId);
            e.HasOne<Space>().WithMany().HasForeignKey(f => f.SpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Folder>().WithMany().HasForeignKey(f => f.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<FileEntry>(e =>
        {
            e.ToTable("files");
            e.Property(f => f.Name).HasMaxLength(ItemName.MaxLength);
            e.Property(f => f.NormalizedName).HasMaxLength(ItemName.MaxLength);
            e.Property(f => f.ConcurrencyStamp).IsConcurrencyToken();
            e.HasIndex(f => new { f.FolderId, f.NormalizedName }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
            e.HasIndex(f => f.SpaceId);
            e.HasIndex(f => f.DeletedRootId);
            e.HasOne<Space>().WithMany().HasForeignKey(f => f.SpaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Folder>().WithMany().HasForeignKey(f => f.FolderId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<FileVersion>(e =>
        {
            e.ToTable("file_versions");
            e.Property(v => v.BlobKey).HasMaxLength(512);
            e.Property(v => v.Sha256).HasMaxLength(64);
            e.Property(v => v.ContentType).HasMaxLength(255);
            e.Property(v => v.ScanStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.ScanDetail).HasMaxLength(500);
            e.Ignore(v => v.IsAvailable);
            e.HasIndex(v => new { v.FileEntryId, v.VersionNo }).IsUnique();
            e.HasIndex(v => v.ScanStatus);
            e.HasOne<FileEntry>().WithMany().HasForeignKey(v => v.FileEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<UploadSession>(e =>
        {
            e.ToTable("upload_sessions");
            e.Property(u => u.FileName).HasMaxLength(ItemName.MaxLength);
            e.HasIndex(u => u.ExpiresAt);
        });

        model.Entity<AclEntry>(e =>
        {
            e.ToTable("acl_entries");
            e.Property(a => a.PrincipalType).HasConversion<string>().HasMaxLength(10);
            e.Property(a => a.PrincipalKey).HasMaxLength(255);
            e.Property(a => a.Permission).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(a => a.SpaceId);
            e.HasOne<Space>().WithMany().HasForeignKey(a => a.SpaceId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(a => a.Action).HasMaxLength(100);
            e.Property(a => a.TargetType).HasMaxLength(50);
            e.Property(a => a.TargetId).HasMaxLength(100);
            e.HasIndex(a => a.OccurredAt);
        });

        model.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}
