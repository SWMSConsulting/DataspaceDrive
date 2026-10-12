using DataspaceDrive.Core.Access;
using DataspaceDrive.Core.Inbox;
using DataspaceDrive.Core.Notifications;
using DataspaceDrive.Core.Shares;
using DataspaceDrive.Core.Users;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Infrastructure.Notifications;

/// <summary>
/// Stellt Benachrichtigungen als E-Mails in die Outbox (gespeichert mit der auslösenden Änderung). Empfänger sind aktive
/// Nutzer mit E-Mail-Adresse, die Benachrichtigungen nicht abgeschaltet haben; Rolleninhaber kennt die Drive aus deren
/// letzter Anmeldung.
/// </summary>
public sealed class Notifier(DriveDbContext db, IOptions<MailOptions> options, TimeProvider clock)
{
    private readonly MailOptions _options = options.Value;

    public async Task NewOffersAsync(string partnerName, IReadOnlyList<IncomingOffer> offers, CancellationToken ct)
    {
        if (!_options.IsConfigured || offers.Count == 0)
            return;
        var titles = string.Join("\n", offers.Select(o => $"  - {o.Title}"));
        await ToRoleAsync(DrivePrincipal.RoleInbox, null, $"Neue Dateien von {partnerName} im Eingang",
            $"{partnerName} hat Dateien an Ihr Unternehmen freigegeben:\n{titles}\n\nAnnehmen oder ablehnen: {Link("/inbox")}", ct);
    }

    public async Task ApprovalRequestedAsync(Share share, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return;
        var requester = await NameAsync(share.CreatedBy, ct);
        await ToRoleAsync(DrivePrincipal.RoleApprover, share.CreatedBy, $"Freigabe zu prüfen: {share.Title}",
            $"{requester} möchte „{share.Title}“ an Partnerunternehmen freigeben.\n\nPrüfen: {Link("/approvals")}", ct);
    }

    public async Task ShareDecidedAsync(Share share, bool approved, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return;
        var decider = share.DecidedBy is { } id ? await NameAsync(id, ct) : "Die Prüfung";
        await ToUserAsync(share.CreatedBy, approved ? $"Freigegeben: {share.Title}" : $"Abgelehnt: {share.Title}",
            approved
                ? $"{decider} hat „{share.Title}“ freigegeben; die Partner erhalten die Dateien jetzt.\n\n{Link("/shares")}"
                : $"{decider} hat „{share.Title}“ abgelehnt{(share.DecisionNote is { } note ? $": {note}" : ".")}\n\n{Link("/shares")}", ct);
    }

    public async Task FirstRetrievalAsync(Guid deliveryId, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return;
        var row = await (from d in db.Deliveries
                         join s in db.Shares on d.ShareId equals s.Id
                         join p in db.Partners on d.PartnerId equals p.Id
                         where d.Id == deliveryId
                         select new { s.CreatedBy, s.Title, p.Name }).FirstOrDefaultAsync(ct);
        if (row is not null)
            await ToUserAsync(row.CreatedBy, $"{row.Name} hat „{row.Title}“ abgerufen",
                $"{row.Name} hat die freigegebenen Dateien „{row.Title}“ über den Dataspace abgerufen.\n\nAbrufprotokoll: {Link("/shares")}", ct);
    }

    public async Task DeliveryFailedAsync(Delivery delivery, CancellationToken ct)
    {
        if (!_options.IsConfigured)
            return;
        var share = await db.Shares.FirstAsync(s => s.Id == delivery.ShareId, ct);
        await ToUserAsync(share.CreatedBy, $"Freigabe nicht bereitgestellt: {share.Title}",
            $"„{share.Title}“ konnte für {delivery.RecipientBpn} nicht im Connector angelegt werden ({delivery.LastError}).\n\n"
            + $"Erneut versuchen: {Link("/shares")}", ct);
    }

    public async Task OfferFinishedAsync(IncomingOffer offer, CancellationToken ct)
    {
        if (!_options.IsConfigured || offer.DecidedBy is not { } userId)
            return;
        var (subject, text) = offer.State switch
        {
            IncomingOfferState.Completed => ($"Übernommen: {offer.Title}", $"„{offer.Title}“ ist geprüft und abgelegt."),
            IncomingOfferState.Quarantined => ($"Gesperrt: {offer.Title}", $"„{offer.Title}“ wurde übernommen, mindestens eine Datei ist nach dem Virenscan gesperrt."),
            _ => ($"Übernahme fehlgeschlagen: {offer.Title}", $"„{offer.Title}“ konnte nicht übernommen werden: {offer.Error}"),
        };
        await ToUserAsync(userId, subject, $"{text}\n\n{Link("/inbox")}", ct);
    }

    private async Task ToRoleAsync(string role, Guid? except, string subject, string body, CancellationToken ct)
    {
        var marker = DriveUser.RoleMarker(role);
        var users = await db.Users.Where(u => u.IsActive && u.EmailNotifications && u.Email != null && u.Roles != null && u.Roles.Contains(marker)
                                              && u.Id != except).ToListAsync(ct);
        foreach (var user in users)
            Enqueue(user.Email!, subject, body);
    }

    private async Task ToUserAsync(Guid userId, string subject, string body, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is { IsActive: true, EmailNotifications: true, Email: not null })
            Enqueue(user.Email, subject, body);
    }

    private void Enqueue(string to, string subject, string body)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        db.MailMessages.Add(new MailMessage
        {
            To = to, Subject = subject.Length > 300 ? subject[..300] : subject,
            Body = (body + "\n\n-- \nDataspaceDrive. Benachrichtigungen lassen sich unter „Einstellungen“ abschalten.") is var b && b.Length > 10000 ? b[..10000] : b,
            CreatedAt = now, NextAttemptAt = now,
        });
    }

    private async Task<string> NameAsync(Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "Jemand";

    private string Link(string path) => string.IsNullOrWhiteSpace(_options.PublicUrl) ? path : _options.PublicUrl.TrimEnd('/') + path;
}
