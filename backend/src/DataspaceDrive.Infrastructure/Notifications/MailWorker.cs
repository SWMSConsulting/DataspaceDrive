using System.Net;
using System.Net.Mail;
using DataspaceDrive.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataspaceDrive.Infrastructure.Notifications;

/// <summary>Versendet die E-Mail-Outbox über SMTP; gesendete Einträge werden nach 30 Tagen gelöscht.</summary>
public sealed class MailWorker(
    IServiceScopeFactory scopes,
    IOptions<MailOptions> options,
    IOptions<Sharing.BackgroundWorkerOptions> workers,
    TimeProvider clock,
    ILogger<MailWorker> logger) : BackgroundService
{
    private const int MaxAttempts = 8;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured || !workers.Value.Enabled)
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "E-Mail-Versand fehlgeschlagen");
            }
            try
            {
                await Task.Delay(Interval, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Sendet fällige E-Mails; liefert die Zahl erfolgreich versendeter.</summary>
    internal async Task<int> SendPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DriveDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;
        var pending = await db.MailMessages.Where(m => m.SentAt == null && m.NextAttemptAt <= now && m.Attempts < MaxAttempts)
            .OrderBy(m => m.Id).Take(50).ToListAsync(ct);
        var sent = 0;
        if (pending.Count > 0)
        {
            var o = options.Value;
            using var smtp = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = o.UseStartTls, DeliveryMethod = SmtpDeliveryMethod.Network };
            if (!string.IsNullOrEmpty(o.Username))
                smtp.Credentials = new NetworkCredential(o.Username, o.Password);
            foreach (var message in pending)
            {
                try
                {
                    using var mail = new System.Net.Mail.MailMessage(new MailAddress(ParseAddress(o.From).Address, ParseAddress(o.From).DisplayName),
                        new MailAddress(message.To)) { Subject = message.Subject, Body = message.Body, IsBodyHtml = false };
                    mail.SubjectEncoding = mail.BodyEncoding = System.Text.Encoding.UTF8;
                    await smtp.SendMailAsync(mail, ct);
                    message.SentAt = clock.GetUtcNow().UtcDateTime;
                    message.LastError = null;
                    sent++;
                }
                catch (Exception ex) when (ex is SmtpException or IOException or FormatException or InvalidOperationException && !ct.IsCancellationRequested)
                {
                    message.Attempts++;
                    message.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                    message.NextAttemptAt = now + TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, message.Attempts)));
                    logger.LogWarning("E-Mail an {To} nicht versendet (Versuch {Attempt}): {Error}", message.To, message.Attempts, ex.Message);
                }
            }
        }
        await db.MailMessages.Where(m => m.SentAt != null && m.SentAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return sent;
    }

    private static MailAddress ParseAddress(string value) => new(value);
}
