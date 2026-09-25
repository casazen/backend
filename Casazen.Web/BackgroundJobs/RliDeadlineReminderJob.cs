using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily reminder of the RLI registration deadline to the landlord (LT-04, A7-04), for every lease not registered yet
/// in any status before registration (from Draft on), with the deadline of <see cref="RliRegistrationDeadline"/>.
/// </summary>
/// <remarks>
/// <para>Thresholds, not exact days (<see cref="Thresholds"/>): the most urgent threshold reached today is sent once per
/// deadline, so a skipped run (or a day without a run) sends it at the next run, and two runs never send it twice.
/// "Overdue" starts the day after the deadline. Each sent threshold is recorded as a <c>DeadlineReminderSent</c> event
/// with payload <c>{threshold}:{deadline}</c>, and only when the email was accepted: a failed send is retried at the
/// next run. A deadline that moves (e.g. an earlier signing date declared later) starts its own thresholds.</para>
/// <para>Questura communication for an extra-EU tenant (LT-07, A7-08, <see cref="QuesturaCommunicationDeadline"/>): its
/// own thresholds on the 48 hours from the delivery of the property (<see cref="QuesturaThresholds"/>), for every lease
/// with an extra-EU tenant (not rejected, not ended) whose communication the landlord has not declared yet, registered
/// leases included. The reminder never ticks the checklist item: only the landlord's declaration does.</para>
/// </remarks>
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

        // Every lease still to be registered (RliRegistrationDeadline.AwaitsRegistration), signed or not.
        var leases = await db.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .Where(l => l.Status != LeaseStatus.Registered && l.Status != LeaseStatus.Rejected)
            .ToListAsync();

        foreach (var lease in leases)
        {
            try
            {
                await RemindRegistrationDeadlineAsync(lease, today);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed RLI reminder for LeaseId={LeaseId}", lease.Id);
            }
        }

        await RemindQuesturaCommunicationsAsync(today);
    }

    /// <summary>
    /// Questura communication (LT-07): every lease with an extra-EU tenant, in any status but Rejected (registered
    /// included: the RLI does not replace it, fiscale.md L14), not ended yet and whose communication was not declared.
    /// </summary>
    private async Task RemindQuesturaCommunicationsAsync(DateTime today)
    {
        var leases = await db.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .Where(l => l.Status != LeaseStatus.Rejected
                && l.QuesturaCommunicationDate == null
                && l.EndDate >= today
                && l.Parties.Any(p => p.Role == PartyRole.Tenant && p.IsExtraEU))
            .ToListAsync();

        foreach (var lease in leases)
        {
            try
            {
                await RemindQuesturaCommunicationAsync(lease, today);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed Questura reminder for LeaseId={LeaseId}", lease.Id);
            }
        }
    }

    private async Task RemindQuesturaCommunicationAsync(LeaseContract lease, DateTime today)
    {
        if (!QuesturaCommunicationDeadline.IsRequired(lease) || lease.QuesturaCommunicationDate is not null)
            return;

        var delivery = QuesturaCommunicationDeadline.DeliveryDate(lease);
        var deadline = QuesturaCommunicationDeadline.Deadline(delivery);
        var daysRemaining = QuesturaCommunicationDeadline.DaysUntil(deadline, today);
        if (QuesturaThresholds.Reached(daysRemaining) is not { } threshold)
            return;

        var propertyName = lease.Property?.Name ?? string.Empty;
        var deliveryDeclared = lease.PropertyDeliveryDate is not null;
        await SendOnceAsync(
            lease,
            QuesturaThresholds.Payload(threshold, deadline),
            () => threshold == QuesturaThresholds.Overdue
                ? EmailTemplates.QuesturaCommunicationOverdue(
                    EmailTemplates.DefaultCulture, propertyName, delivery, deadline, deliveryDeclared)
                : EmailTemplates.QuesturaCommunicationReminder(
                    EmailTemplates.DefaultCulture, propertyName, delivery, deadline, deliveryDeclared));
    }

    private async Task RemindRegistrationDeadlineAsync(LeaseContract lease, DateTime today)
    {
        if (RliRegistrationDeadline.Resolve(lease, today) is not { } deadline)
        {
            // Not signed yet with the start date still ahead: the deadline is at least 30 days away. Signed without a
            // recorded stipula (older leases): to be determined, never guessed.
            if (!RliRegistrationDeadline.IsBeforeFullSignature(lease.Status))
            {
                logger.LogWarning(
                    "No RLI reminder for LeaseId={LeaseId}: signed without a recorded stipula date, deadline to be determined",
                    lease.Id);
            }

            return;
        }

        var daysRemaining = RliRegistrationDeadline.DaysRemaining(deadline, today);
        if (Thresholds.Reached(daysRemaining) is not { } threshold)
            return;

        var propertyName = lease.Property?.Name ?? string.Empty;
        var notSignedYet = RliRegistrationDeadline.IsBeforeFullSignature(lease.Status);
        await SendOnceAsync(
            lease,
            Thresholds.Payload(threshold, deadline),
            () => threshold == Thresholds.Overdue
                ? EmailTemplates.RliDeadlineOverdue(EmailTemplates.DefaultCulture, propertyName, deadline, notSignedYet)
                : EmailTemplates.RliDeadlineReminder(
                    EmailTemplates.DefaultCulture, propertyName, deadline, daysRemaining, notSignedYet));
    }

    private async Task SendOnceAsync(LeaseContract lease, string payload, Func<EmailContent> render)
    {
        if (lease.Events.Any(e => e.EventType == LeaseEventType.DeadlineReminderSent && e.Payload == payload))
            return;

        var to = lease.Parties.FirstOrDefault(p => p.Role == PartyRole.Landlord)?.ContactEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogInformation("Skip RLI reminder {Payload} for LeaseId={LeaseId}: no landlord email", payload, lease.Id);
            return;
        }

        // Already inside a Hangfire job: sent directly (FD-13).
        var content = render();
        var result = await emailService.SendEmailAsync(to, content.Subject, content.HtmlBody);
        if (!result.Success)
        {
            // Not recorded: the same threshold is tried again at the next run.
            logger.LogWarning(
                "RLI reminder {Payload} for LeaseId={LeaseId} not sent: {ErrorDetail}",
                payload,
                lease.Id,
                result.ErrorDetail);
            return;
        }

        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.DeadlineReminderSent,
            OccurredAt = _clock.GetUtcNow().UtcDateTime,
            Payload = payload,
        });
        await db.SaveChangesAsync();
        logger.LogInformation("Sent RLI reminder {Payload} for LeaseId={LeaseId}", payload, lease.Id);
    }

    /// <summary>
    /// Reminder thresholds of the RLI deadline (spec RLI-AC6: 15, 7 and 1 days before, then overdue). Product choice,
    /// not a regulatory value.
    /// </summary>
    public static class Thresholds
    {
        public const string Days15 = "t-15";
        public const string Days7 = "t-7";
        public const string Days1 = "t-1";
        public const string Overdue = "overdue";

        /// <summary>
        /// The most urgent threshold reached with <paramref name="daysRemaining"/> days to the deadline: ≤ 15, ≤ 7,
        /// ≤ 1 (the deadline day included), overdue from the day after; null more than 15 days before.
        /// </summary>
        public static string? Reached(int daysRemaining) => daysRemaining switch
        {
            < 0 => Overdue,
            <= 1 => Days1,
            <= 7 => Days7,
            <= 15 => Days15,
            _ => null,
        };

        /// <summary>Event payload of a threshold sent for <paramref name="deadline"/>, e.g. <c>t-7:2026-08-31</c>.</summary>
        public static string Payload(string threshold, DateTime deadline) =>
            $"{threshold}:{RomeCalendar.DateInRome(deadline).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Reminder thresholds of the Questura communication (LT-07), on the Rome calendar, relative to the day the 48 hours
    /// from the delivery end (<see cref="QuesturaCommunicationDeadline.Deadline(DateTime)"/> = delivery + 2 days). Product
    /// choice, not a regulatory value: a few days before the delivery, at the delivery, on the last day, then overdue.
    /// Same logic as <see cref="Thresholds"/>: the most urgent threshold reached today, once per deadline.
    /// </summary>
    public static class QuesturaThresholds
    {
        /// <summary>1 to 3 days before the delivery day.</summary>
        public const string BeforeDelivery = "questura-before-delivery";

        /// <summary>The delivery day and the day after.</summary>
        public const string Delivery = "questura-delivery";

        /// <summary>The day the 48 hours end.</summary>
        public const string Deadline = "questura-deadline";

        /// <summary>From the day after the deadline, while the communication is not declared.</summary>
        public const string Overdue = "questura-overdue";

        /// <summary>Days before the delivery day of the first reminder.</summary>
        public const int DaysBeforeDelivery = 3;

        /// <summary>
        /// The most urgent threshold reached with <paramref name="daysToDeadline"/> days to the deadline (delivery + 2):
        /// overdue below 0, the deadline day at 0, the delivery day and the day after at 2 and 1, up to
        /// <see cref="DaysBeforeDelivery"/> days before the delivery; null earlier.
        /// </summary>
        public static string? Reached(int daysToDeadline) => daysToDeadline switch
        {
            < 0 => Overdue,
            0 => Deadline,
            <= QuesturaCommunicationDeadline.DaysFromDelivery => Delivery,
            <= QuesturaCommunicationDeadline.DaysFromDelivery + DaysBeforeDelivery => BeforeDelivery,
            _ => null,
        };

        /// <summary>Event payload of a threshold sent for <paramref name="deadline"/>, e.g. <c>questura-delivery:2026-10-03</c>.</summary>
        public static string Payload(string threshold, DateTime deadline) => Thresholds.Payload(threshold, deadline);
    }
}
