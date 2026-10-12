using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Partners;
using DataspaceDrive.Core.Shares;
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
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Share> Shares => Set<Share>();
    public DbSet<ShareItem> ShareItems => Set<ShareItem>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<DeliveryRetrieval> DeliveryRetrievals => Set<DeliveryRetrieval>();
    public DbSet<DeliveryTask> DeliveryTasks => Set<DeliveryTask>();
    public DbSet<IncomingOffer> IncomingOffers => Set<IncomingOffer>();
    public DbSet<IncomingItem> IncomingItems => Set<IncomingItem>();

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

        model.Entity<Partner>(e =>
        {
            e.ToTable("partners");
            e.HasIndex(p => p.Bpn).IsUnique();
            e.Property(p => p.Bpn).HasMaxLength(16);
            e.Property(p => p.Name).HasMaxLength(200);
            e.Property(p => p.DspAddress).HasMaxLength(500);
            e.Property(p => p.Did).HasMaxLength(500);
            e.Property(p => p.Protocol).HasMaxLength(100);
            e.Property(p => p.ProtocolAddress).HasMaxLength(500);
            e.Property(p => p.ResolveError).HasMaxLength(1000);
            e.Property(p => p.LastPollError).HasMaxLength(1000);
            e.Ignore(p => p.IsResolved);
        });

        model.Entity<Share>(e =>
        {
            e.ToTable("shares");
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.HasIndex(x => x.CreatedBy);
            e.HasOne<DriveUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ShareItem>(e =>
        {
            e.ToTable("share_items");
            e.Property(i => i.ItemKey).HasMaxLength(20);
            e.Property(i => i.Name).HasMaxLength(ItemName.MaxLength);
            e.Property(i => i.Sha256).HasMaxLength(64);
            e.Property(i => i.ContentType).HasMaxLength(255);
            e.Property(i => i.BlobKey).HasMaxLength(512);
            e.HasIndex(i => new { i.ShareId, i.ItemKey }).IsUnique();
            e.HasIndex(i => i.FileVersionId);
            e.HasOne<Share>().WithMany().HasForeignKey(i => i.ShareId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FileVersion>().WithMany().HasForeignKey(i => i.FileVersionId).OnDelete(DeleteBehavior.SetNull);
        });

        model.Entity<Delivery>(e =>
        {
            e.ToTable("deliveries");
            e.Property(d => d.RecipientBpn).HasMaxLength(16);
            e.Property(d => d.State).HasConversion<string>().HasMaxLength(30);
            e.Property(d => d.LastError).HasMaxLength(1000);
            e.Property(d => d.ConcurrencyStamp).IsConcurrencyToken();
            e.Ignore(d => d.ExternalId);
            e.HasIndex(d => d.ShareId);
            e.HasIndex(d => new { d.State, d.ExpiresAt });
            e.HasOne<Share>().WithMany().HasForeignKey(d => d.ShareId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Partner>().WithMany().HasForeignKey(d => d.PartnerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<DeliveryRetrieval>(e =>
        {
            e.ToTable("delivery_retrievals");
            e.Property(r => r.Decision).HasMaxLength(40);
            e.Property(r => r.ConsumerBpn).HasMaxLength(100);
            e.Property(r => r.AgreementId).HasMaxLength(200);
            e.Property(r => r.ItemKey).HasMaxLength(20);
            e.HasIndex(r => new { r.DeliveryId, r.OccurredAt });
            e.HasOne<Delivery>().WithMany().HasForeignKey(r => r.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DeliveryTask>(e =>
        {
            e.ToTable("delivery_tasks");
            e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.LastError).HasMaxLength(1000);
            e.HasIndex(t => new { t.CompletedAt, t.NextAttemptAt });
            e.HasOne<Delivery>().WithMany().HasForeignKey(t => t.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<IncomingOffer>(e =>
        {
            e.ToTable("incoming_offers");
            e.Property(o => o.ProviderBpn).HasMaxLength(16);
            e.Property(o => o.AssetId).HasMaxLength(200);
            e.Property(o => o.OfferId).HasMaxLength(2000);
            e.Property(o => o.Title).HasMaxLength(200);
            e.Property(o => o.Message).HasMaxLength(2000);
            e.Property(o => o.SenderOrganization).HasMaxLength(200);
            e.Property(o => o.State).HasConversion<string>().HasMaxLength(30);
            e.Property(o => o.NegotiationId).HasMaxLength(200);
            e.Property(o => o.AgreementId).HasMaxLength(200);
            e.Property(o => o.TransferProcessId).HasMaxLength(200);
            e.Property(o => o.Error).HasMaxLength(1000);
            e.Property(o => o.ConcurrencyStamp).IsConcurrencyToken();
            e.HasIndex(o => new { o.PartnerId, o.AssetId }).IsUnique();
            e.HasIndex(o => o.State);
            e.HasOne<Partner>().WithMany().HasForeignKey(o => o.PartnerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<IncomingItem>(e =>
        {
            e.ToTable("incoming_items");
            e.Property(i => i.ItemKey).HasMaxLength(20);
            e.Property(i => i.Name).HasMaxLength(ItemName.MaxLength);
            e.Property(i => i.Sha256).HasMaxLength(64);
            e.Property(i => i.ContentType).HasMaxLength(255);
            e.HasIndex(i => new { i.OfferId, i.ItemKey }).IsUnique();
            e.HasOne<IncomingOffer>().WithMany().HasForeignKey(i => i.OfferId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
