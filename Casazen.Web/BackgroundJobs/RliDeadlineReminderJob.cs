using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

public class RliDeadlineReminderJob(
    AppDbContext db,
    IEmailService emailService,
    ILogger<RliDeadlineReminderJob> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(timeoutInSeconds: 120)]
    public async Task ExecuteAsync()
    {
        var today = _clock.TodayInRome();
        var leases = await db.LeaseContracts
            .Include(l => l.Property)
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

                var propertyName = lease.Property?.Name ?? string.Empty;
                if (milestone is not null)
                {
                    // Localized templates (FD-13), not inline HTML: the reminder no longer shows a technical
                    // milestone code or the lease id (A7-26).
                    var content = milestone == "overdue"
                        ? EmailTemplates.RliDeadlineOverdue(EmailTemplates.DefaultCulture, propertyName, lease.RegistrationDeadline)
                        : EmailTemplates.RliDeadlineReminder(
                            EmailTemplates.DefaultCulture, propertyName, lease.RegistrationDeadline, days);
                    await SendOnceAsync(lease, milestone, content);
                }

                if (lease.HasExtraEUTenant)
                    await SendOnceAsync(lease, "extra-eu", EmailTemplates.RliExtraEuNotice(EmailTemplates.DefaultCulture, propertyName));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed RLI reminder for LeaseId={LeaseId}", lease.Id);
            }
        }
    }

    private async Task SendOnceAsync(LeaseContract lease, string payload, EmailContent content)
    {
        if (lease.Events.Any(e => e.EventType == LeaseEventType.DeadlineReminderSent && e.Payload == payload))
            return;

        var to = lease.Parties.FirstOrDefault(p => p.Role == PartyRole.Landlord)?.ContactEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogInformation("Skip RLI reminder {Payload} for LeaseId={LeaseId}: no landlord email", payload, lease.Id);
            return;
        }

        await emailService.SendEmailAsync(to, content.Subject, content.HtmlBody);
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.DeadlineReminderSent,
            Payload = payload,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Sent RLI reminder {Payload} for LeaseId={LeaseId}", payload, lease.Id);
    }
}
