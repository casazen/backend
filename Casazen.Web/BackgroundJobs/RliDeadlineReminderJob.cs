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
/// with payload <c>{threshold}:{deadline}:{partyId}</c>, and only when the email was accepted: a failed send is retried at the
/// next run. A deadline that moves (e.g. an earlier signing date declared later) starts its own thresholds. Every
/// landlord of the lease receives it (LT-14), recorded per landlord.</para>
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

        var leases = await db.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .Where(l => l.Status != LeaseStatus.Registered && l.Status != LeaseStatus.Rejected && l.PartiesAnonymizedAt == null
                && (l.DraftData == null || l.Status != LeaseStatus.Draft))
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
    /// included: the RLI does not replace it, fiscale.md L14), not ended yet (the end of the period it is in, LR-14) and whose
    /// communication was not declared.
    /// </summary>
    private async Task RemindQuesturaCommunicationsAsync(DateTime today)
    {
        var leases = await db.LeaseContracts
            .Include(l => l.Property)
            .Include(l => l.Parties)
            .Include(l => l.Events)
            .Where(l => l.Status != LeaseStatus.Rejected
                && l.QuesturaCommunicationDate == null
                && l.CurrentTermEndDate >= today
                && (l.DraftData == null || l.Status != LeaseStatus.Draft)
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
        var sentPayloads = lease.Events
            .Where(e => e.EventType == LeaseEventType.DeadlineReminderSent && e.Payload is not null)
            .Select(e => e.Payload!)
            .ToHashSet(StringComparer.Ordinal);
        if (sentPayloads.Contains(payload))
            return;

        var landlords = lease.Parties
            .Where(p => p.Role == PartyRole.Landlord && p.AnonymizedAt == null && !string.IsNullOrWhiteSpace(p.ContactEmail))
            .OrderBy(p => p.Position)
            .DistinctBy(p => p.ContactEmail.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (landlords.Count == 0)
        {
            logger.LogInformation("Skip RLI reminder {Payload} for LeaseId={LeaseId}: no landlord email", payload, lease.Id);
            return;
        }

        EmailContent? content = null;
        foreach (var landlord in landlords)
        {
            var landlordPayload = $"{payload}:{landlord.Id:N}";
            if (sentPayloads.Contains(landlordPayload))
                continue;

            content ??= render();
            var result = await emailService.SendEmailAsync(landlord.ContactEmail.Trim(), content.Subject, content.HtmlBody);
            if (!result.Success)
            {
                logger.LogWarning(
                    "RLI reminder {Payload} for LeaseId={LeaseId} PartyId={PartyId} not sent: {ErrorDetail}",
                    payload,
                    lease.Id,
                    landlord.Id,
                    result.ErrorDetail);
                continue;
            }

            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = lease.Id,
                EventType = LeaseEventType.DeadlineReminderSent,
                OccurredAt = _clock.GetUtcNow().UtcDateTime,
                Payload = landlordPayload,
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Sent RLI reminder {Payload} for LeaseId={LeaseId} PartyId={PartyId}", payload, lease.Id, landlord.Id);
        }
    }

    public static class Thresholds
    {
        public const string Days15 = "t-15";
        public const string Days7 = "t-7";
        public const string Days1 = "t-1";
        public const string Overdue = "overdue";

        public static string? Reached(int daysRemaining) => daysRemaining switch
        {
            < 0 => Overdue,
            <= 1 => Days1,
            <= 7 => Days7,
            <= 15 => Days15,
            _ => null,
        };

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
        public const string BeforeDelivery = "questura-before-delivery";

        public const string Delivery = "questura-delivery";

        public const string Deadline = "questura-deadline";

        public const string Overdue = "questura-overdue";

        public const int DaysBeforeDelivery = 3;

        public static string? Reached(int daysToDeadline) => daysToDeadline switch
        {
            < 0 => Overdue,
            0 => Deadline,
            <= QuesturaCommunicationDeadline.DaysFromDelivery => Delivery,
            <= QuesturaCommunicationDeadline.DaysFromDelivery + DaysBeforeDelivery => BeforeDelivery,
            _ => null,
        };

        public static string Payload(string threshold, DateTime deadline) => Thresholds.Payload(threshold, deadline);
    }
}
