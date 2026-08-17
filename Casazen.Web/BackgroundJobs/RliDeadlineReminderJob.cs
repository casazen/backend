using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

public class RliDeadlineReminderJob(
    AppDbContext db,
    IEmailService emailService,
    ILogger<RliDeadlineReminderJob> logger)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public async Task ExecuteAsync()
    {
        var today = DateTime.UtcNow.Date;
        var leases = await db.LeaseContracts
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .Include(l => l.Registration)
            .Where(l =>
                l.Status == LeaseStatus.Signed
                || l.Status == LeaseStatus.RegistrationPending
                || l.Status == LeaseStatus.SentToProvider)
            .ToListAsync();

        foreach (var lease in leases)
        {
            try
            {
                var days = (lease.RegistrationDeadline.Date - today).Days;
                var milestone = days switch
                {
                    15 => "t-15",
                    7 => "t-7",
                    1 => "t-1",
                    <= 0 => "overdue",
                    _ => null,
                };

                if (milestone is not null)
                    await SendOnceAsync(lease, milestone, BuildDeadlineSubject(lease, milestone), BuildDeadlineHtml(lease, milestone));

                if (lease.HasExtraEUTenant)
                    await SendOnceAsync(lease, "extra-eu", BuildExtraEuSubject(lease), BuildExtraEuHtml(lease));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed RLI reminder for LeaseId={LeaseId}", lease.Id);
            }
        }
    }

    private async Task SendOnceAsync(LeaseContract lease, string payload, string subject, string html)
    {
        if (lease.Events.Any(e => e.EventType == LeaseEventType.DeadlineReminderSent && e.Payload == payload))
            return;

        var to = lease.Parties.FirstOrDefault(p => p.Role == PartyRole.Landlord)?.ContactEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogInformation("Skip RLI reminder {Payload} for LeaseId={LeaseId}: no landlord email", payload, lease.Id);
            return;
        }

        await emailService.SendEmailAsync(to, subject, html);
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.DeadlineReminderSent,
            Payload = payload,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Sent RLI reminder {Payload} for LeaseId={LeaseId}", payload, lease.Id);
    }

    private static string BuildDeadlineSubject(LeaseContract lease, string milestone) =>
        milestone == "overdue"
            ? $"RLI scaduta — contratto {lease.Id:N}"
            : $"Promemoria RLI ({milestone}) — scadenza {lease.RegistrationDeadline:dd/MM/yyyy}";

    private static string BuildDeadlineHtml(LeaseContract lease, string milestone) =>
        $"<p>Promemoria registrazione RLI (scadenza {lease.RegistrationDeadline:dd/MM/yyyy}, milestone {milestone}).</p>" +
        "<p>CasaZen non deposita in automatico. La responsabilita del filing resta al locatore / intermediario abilitato. Bozza da confermare con legale.</p>";

    private static string BuildExtraEuSubject(LeaseContract lease) =>
        $"Questura / cessione di fabbricato — contratto {lease.Id:N}";

    private static string BuildExtraEuHtml(LeaseContract lease) =>
        "<p>Il contratto include un conduttore extra-UE. Verificare la comunicazione in Questura (Art. 7 D.Lgs 286/1998).</p>" +
        "<p>Testo bozza da confermare con legale. CasaZen non invia la comunicazione.</p>";
}
