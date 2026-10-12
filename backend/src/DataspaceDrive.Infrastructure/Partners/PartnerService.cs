using System.Text.RegularExpressions;
using DataspaceDrive.Core;
using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Audit;
using DataspaceDrive.Core.Partners;
using DataspaceDrive.Core.Files;
using DataspaceDrive.Infrastructure.Files;
using DataspaceDrive.Infrastructure.Persistence;
using DataspaceDrive.Infrastructure.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Swms.Edc.Client;

namespace DataspaceDrive.Infrastructure.Partners;

public sealed partial class PartnerService(
    DriveDbContext db,
    AccessResolver access,
    IServiceProvider services,
    IOptions<ParticipantOptions> participant,
    TimeProvider clock) : IPartnerService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PartnerInfo>> ListAsync(DrivePrincipal admin, CancellationToken ct)
    {
        RequireAdmin(admin);
        return await InfosAsync(await db.Partners.OrderBy(p => p.Name).ToListAsync(ct), ct);
    }

    public async Task<PartnerInfo> CreateAsync(DrivePrincipal admin, PartnerRequest request, CancellationToken ct)
    {
        RequireAdmin(admin);
        var (bpn, name, address) = Validate(request);
        if (await db.Partners.AnyAsync(p => p.Bpn == bpn, ct))
            throw new ConflictException($"Der Partner {bpn} ist bereits eingetragen.");
        var partner = new Partner
        {
            Id = Guid.NewGuid(), Bpn = bpn, Name = name, DspAddress = address, SharingEnabled = request.SharingEnabled,
            ReceivingEnabled = request.ReceivingEnabled, Blocked = request.Blocked, CreatedAt = Now,
        };
        await ApplyAutoAcceptAsync(admin, partner, request, ct);
        await ResolveIntoAsync(partner, ct);
        db.Partners.Add(partner);
        Audit(admin, "partner.created", partner, $"{bpn} {name} {address}, automatisch annehmen: {partner.AutoAccept}");
        await db.SaveChangesAsync(ct);
        return (await InfosAsync([partner], ct))[0];
    }

    public async Task<PartnerInfo> UpdateAsync(DrivePrincipal admin, Guid partnerId, PartnerRequest request, CancellationToken ct)
    {
        RequireAdmin(admin);
        var partner = await FindAsync(partnerId, ct);
        var (bpn, name, address) = Validate(request);
        if (bpn != partner.Bpn)
            throw new InvalidInputException("Die BPN eines Partners lässt sich nicht ändern; bitte neu anlegen.");
        var readdress = address != partner.DspAddress;
        partner.Name = name;
        partner.DspAddress = address;
        partner.SharingEnabled = request.SharingEnabled;
        partner.ReceivingEnabled = request.ReceivingEnabled;
        partner.Blocked = request.Blocked;
        await ApplyAutoAcceptAsync(admin, partner, request, ct);
        if (readdress || !partner.IsResolved)
            await ResolveIntoAsync(partner, ct);
        Audit(admin, "partner.updated", partner,
            $"{name} {address} teilen={request.SharingEnabled} empfangen={request.ReceivingEnabled} gesperrt={request.Blocked} automatisch={partner.AutoAccept}");
        await db.SaveChangesAsync(ct);
        return (await InfosAsync([partner], ct))[0];
    }

    public async Task DeleteAsync(DrivePrincipal admin, Guid partnerId, CancellationToken ct)
    {
        RequireAdmin(admin);
        var partner = await FindAsync(partnerId, ct);
        if (await db.Deliveries.AnyAsync(d => d.PartnerId == partner.Id, ct) || await db.IncomingOffers.AnyAsync(o => o.PartnerId == partner.Id, ct))
            throw new ConflictException("Mit diesem Partner gab es bereits Austausch; er kann nur gesperrt, nicht gelöscht werden.");
        db.Partners.Remove(partner);
        Audit(admin, "partner.deleted", partner, partner.Bpn);
        await db.SaveChangesAsync(ct);
    }

    public async Task<PartnerInfo> ResolveAsync(DrivePrincipal admin, Guid partnerId, CancellationToken ct)
    {
        RequireAdmin(admin);
        var partner = await FindAsync(partnerId, ct);
        await ResolveIntoAsync(partner, ct);
        await db.SaveChangesAsync(ct);
        return (await InfosAsync([partner], ct))[0];
    }

    public async Task<IReadOnlyList<ShareTarget>> ShareTargetsAsync(DrivePrincipal principal, CancellationToken ct)
    {
        if (!principal.Roles.Contains(DrivePrincipal.RoleExternalShare))
            throw new AccessDeniedException("Freigaben an andere Unternehmen sind für dieses Konto nicht freigeschaltet.");
        return await db.Partners
            .Where(p => p.SharingEnabled && !p.Blocked && p.Did != null && p.ProtocolAddress != null)
            .OrderBy(p => p.Name)
            .Select(p => new ShareTarget(p.Id, p.Bpn, p.Name))
            .ToListAsync(ct);
    }

    /// <summary>Fragt den eigenen Connector nach DID, Protokoll und versionsspezifischer Adresse des Partners.</summary>
    private async Task ResolveIntoAsync(Partner partner, CancellationToken ct)
    {
        var edc = services.GetRequiredService<EdcConnection>().TryClient;
        if (edc is null)
        {
            partner.ResolveError = "Kein EDC konfiguriert (Drive:Edc:ManagementUrl).";
            return;
        }
        try
        {
            var dsp = await edc.DiscoverDspAsync(partner.Bpn, partner.DspAddress, ct);
            partner.Did = dsp.CounterPartyId;
            partner.Protocol = dsp.Protocol;
            partner.ProtocolAddress = dsp.CounterPartyAddress;
            partner.ResolvedAt = Now;
            partner.ResolveError = null;
        }
        catch (Exception ex) when (ex is EdcException or HttpRequestException or TaskCanceledException)
        {
            partner.ResolveError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }
    }

    private (string Bpn, string Name, string Address) Validate(PartnerRequest request)
    {
        var bpn = (request.Bpn ?? "").Trim().ToUpperInvariant();
        if (!BpnPattern().IsMatch(bpn))
            throw new InvalidInputException("Die BPN muss die Form BPNL + 12 Zeichen haben, z. B. BPNL00000000WB02.");
        if (string.Equals(bpn, participant.Value.Bpn, StringComparison.OrdinalIgnoreCase))
            throw new InvalidInputException("Das eigene Unternehmen kann kein Partner sein.");
        var name = (request.Name ?? "").Trim();
        if (name.Length is 0 or > 200)
            throw new InvalidInputException("Der Name ist Pflicht und höchstens 200 Zeichen lang.");
        var address = (request.DspAddress ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || address.Length > 500)
            throw new InvalidInputException("Die DSP-Adresse muss eine absolute http(s)-URL sein, z. B. https://partner-edc/api/v1/dsp.");
        return (bpn, name, address);
    }

    private async Task<Partner> FindAsync(Guid partnerId, CancellationToken ct) =>
        await db.Partners.FirstOrDefaultAsync(p => p.Id == partnerId, ct) ?? throw new NotFoundException("Der Partner wurde nicht gefunden.");

    private static void RequireAdmin(DrivePrincipal principal)
    {
        if (!principal.IsAdmin)
            throw new AccessDeniedException("Partner dürfen nur Administratoren verwalten.");
    }

    private void Audit(DrivePrincipal principal, string action, Partner partner, string details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = Now, ActorUserId = principal.UserId, Action = action, TargetType = "partner", TargetId = partner.Id.ToString(),
            Details = details,
        });

    /// <summary>
    /// Automatische Annahme: „Alle“ braucht einen Zielordner, auf den die Person Schreibrecht hat; abgelegt wird in ihrem Namen.
    /// </summary>
    private async Task ApplyAutoAcceptAsync(DrivePrincipal admin, Partner partner, PartnerRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.AutoAccept))
            throw new InvalidInputException("Unbekannte Einstellung für die automatische Annahme.");
        partner.AutoAccept = request.AutoAccept;
        if (request.AutoAccept != AutoAcceptMode.All)
        {
            partner.AutoAcceptFolderId = null;
            partner.AutoAcceptUserId = null;
            return;
        }
        if (request.AutoAcceptFolderId is not { } folderId)
            throw new InvalidInputException("Für „Alle automatisch annehmen“ bitte einen Zielordner wählen.");
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.DeletedAt == null, ct)
            ?? throw new NotFoundException("Der Zielordner wurde nicht gefunden.");
        await access.RequireAsync(admin, folder.SpaceId, Permission.Write, ct);
        partner.AutoAcceptFolderId = folder.Id;
        partner.AutoAcceptUserId = admin.UserId;
    }

    private async Task<IReadOnlyList<PartnerInfo>> InfosAsync(IReadOnlyList<Partner> partners, CancellationToken ct)
    {
        var folderIds = partners.Where(p => p.AutoAcceptFolderId != null).Select(p => p.AutoAcceptFolderId!.Value).Distinct().ToList();
        var folders = await (from f in db.Folders
                             join sp in db.Spaces on f.SpaceId equals sp.Id
                             where folderIds.Contains(f.Id)
                             select new { f.Id, f.Name, f.ParentId, Space = sp.Name }).ToListAsync(ct);
        var userIds = partners.Where(p => p.AutoAcceptUserId != null).Select(p => p.AutoAcceptUserId!.Value).Distinct().ToList();
        var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        string? FolderPath(Guid? id) => folders.FirstOrDefault(f => f.Id == id) is { } f ? (f.ParentId is null ? f.Space : $"{f.Space} / … / {f.Name}") : null;
        return partners.Select(p => new PartnerInfo(p.Id, p.Bpn, p.Name, p.DspAddress, p.Did, p.Protocol, p.ResolvedAt, p.ResolveError,
                p.SharingEnabled, p.ReceivingEnabled, p.Blocked, p.LastPolledAt, p.LastPollError, p.AutoAccept, p.AutoAcceptFolderId,
                FolderPath(p.AutoAcceptFolderId), p.AutoAcceptUserId is { } u ? users.GetValueOrDefault(u) : null))
            .ToList();
    }

    [GeneratedRegex("^BPNL[0-9A-Z]{12}$")]
    private static partial Regex BpnPattern();
}
