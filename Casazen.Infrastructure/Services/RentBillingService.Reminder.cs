using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The landlord's reminder of an installment that is not paid (LR-01, B1, decision D30): an email to the tenants, never an SMS
/// nor an automatic one. It runs under the same lock of the lease as every other change of its installments, so it cannot
/// cross a payment (the tenant's session, the webhook, an offline payment) nor another reminder of the same installment.
/// </summary>
public sealed partial class RentBillingService
{
    public async Task<RentReminderResult> SendReminderAsync(
        Guid leaseId, Guid installmentId, string? note, CancellationToken cancellationToken = default)
    {
        var cleanNote = NormalizeReminderNote(note);
        var intervalHours = RentCharges.GetReminderIntervalHours(configuration);
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);

        ReminderClaim claim;
        InstallmentFacts facts;
        string? token = null;
        await using (var transaction = await LockLeaseAsync(leaseId, cancellationToken))
        {
            // Read again under the lock: a payment, a reminder or the end of the schedule may have come meanwhile.
            var entry = await FindEntryAsync(leaseId, installmentId, cancellationToken);
            var schedule = await db.RentSchedules.AsNoTracking().SingleAsync(s => s.Id == entry.RentScheduleId, cancellationToken);
            EnsurePayable(entry, schedule);

            if (Tenants(lease).Count == 0)
                throw new DomainRuleException(RentBillingErrorCodes.NoTenantEmail, "RentNoTenantEmail");

            // The microsecond precision of the database: the value compared back after a round trip is the one written.
            var now = TruncateToMicroseconds(UtcNow);
            if (entry.LastReminderAt is { } last && now < last.AddHours(intervalHours))
            {
                throw new DomainRuleException(
                    RentRegisterErrorCodes.ReminderTooSoon, "RentReminderTooSoon", intervalHours);
            }

            claim = new ReminderClaim(now, entry.LastReminderAt, entry.ReminderCount, entry.PaymentTokenHash, entry.PaymentRequestedAt);

            // With Connect the reminder carries a new personal link (the previous one stops working, as for the payment
            // request); without it the tenant pays the way agreed with the landlord (D30).
            if (OnlinePaymentsAvailable(lease.Org))
                token = IssueToken(entry);

            entry.LastReminderAt = now;
            entry.ReminderCount++;
            entry.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            claim = claim with { ReminderCount = entry.ReminderCount, NewTokenHash = token is null ? null : entry.PaymentTokenHash };
            facts = new InstallmentFacts(
                entry.PeriodStart,
                entry.PeriodEnd,
                entry.DueDate,
                entry.AmountDue,
                RentInstallmentRules.IsOverdue(entry.Status, entry.DueDate, Today));
        }

        var recipients = QueueReminder(lease, installmentId, facts, token, cleanNote);
        if (recipients == 0)
        {
            // Nothing left the platform (email provider not configured, queue down): the reminder did not happen, so it is not
            // recorded, and the link that was just replaced works again.
            await RestoreReminderAsync(leaseId, installmentId, claim, cancellationToken);
            throw new DomainRuleException(RentRegisterErrorCodes.ReminderNotSent, "RentReminderNotSent");
        }

        // Ids and counts only: no name, address nor note in the log.
        logger.LogInformation(
            "Rent reminder {ReminderCount} of installment {InstallmentId} (lease {LeaseId}) queued for {Recipients} tenants, link {WithLink}",
            claim.ReminderCount,
            installmentId,
            leaseId,
            recipients,
            token is not null);
        return new RentReminderResult(
            installmentId, claim.SentAt, claim.ReminderCount, recipients, token is not null, claim.SentAt.AddHours(intervalHours));
    }

    /// <summary>The note trimmed, <c>null</c> when blank; 422 when longer than <see cref="RentCharges.MaxReminderNoteLength"/>.</summary>
    private static string? NormalizeReminderNote(string? note)
    {
        var text = note?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > RentCharges.MaxReminderNoteLength)
            throw new DomainRuleException(RentRegisterErrorCodes.ReminderNoteInvalid, "RentReminderNoteTooLong");

        return text;
    }

    /// <summary>Queues the reminder to every tenant with an address; the number of emails queued.</summary>
    private int QueueReminder(LeaseContract lease, Guid installmentId, InstallmentFacts facts, string? token, string? note)
    {
        var payUrl = token is null ? null : links.RentPayment(installmentId, token);
        var queued = 0;
        foreach (var tenant in Tenants(lease))
        {
            var content = EmailTemplates.RentReminder(
                EmailTemplates.DefaultCulture,
                FullName(tenant),
                lease.Property?.Name ?? string.Empty,
                lease.Org.Name,
                facts.PeriodStart,
                facts.PeriodEnd,
                facts.DueDate,
                facts.Amount,
                facts.Overdue,
                note,
                payUrl);
            if (emailQueue.Enqueue(tenant.ContactEmail, content, EmailTemplates.Names.RentReminder))
                queued++;
        }

        return queued;
    }

    /// <summary>
    /// Takes the reminder back after the email could not be queued: the previous <c>LastReminderAt</c> and count, and, if a new
    /// link was issued for it and nothing else replaced it since, the previous link. Under the lock of the lease.
    /// </summary>
    private async Task RestoreReminderAsync(Guid leaseId, Guid installmentId, ReminderClaim claim, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await LockLeaseAsync(leaseId, cancellationToken);
        var entry = await FindEntryAsync(leaseId, installmentId, cancellationToken);
        if (entry.LastReminderAt == claim.SentAt)
        {
            entry.LastReminderAt = claim.PreviousReminderAt;
            entry.ReminderCount = claim.PreviousReminderCount;
            if (claim.NewTokenHash is not null && entry.PaymentTokenHash == claim.NewTokenHash)
            {
                entry.PaymentTokenHash = claim.PreviousTokenHash;
                entry.PaymentRequestedAt = claim.PreviousRequestedAt;
            }

            entry.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % (TimeSpan.TicksPerMillisecond / 1000), DateTimeKind.Utc);

    /// <summary>What the installment was before the reminder, to put it back if the email cannot be queued.</summary>
    private sealed record ReminderClaim(
        DateTime SentAt,
        DateTime? PreviousReminderAt,
        int PreviousReminderCount,
        string? PreviousTokenHash,
        DateTime? PreviousRequestedAt)
    {
        /// <summary>Reminders recorded with this one (after the increment).</summary>
        public int ReminderCount { get; init; }

        /// <summary>Hash of the link issued with the reminder; <c>null</c> when the reminder carries no link.</summary>
        public string? NewTokenHash { get; init; }
    }

    /// <summary>What the email says about the installment.</summary>
    private sealed record InstallmentFacts(DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueDate, decimal Amount, bool Overdue);
}
