using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The landlord's reminder of an installment that is not paid (LR-01, B1, decision D30): an email or a WhatsApp message to
/// the tenants, never an SMS nor an automatic one. The change of the installment runs under the same lock of the lease as
/// every other change, so it cannot cross a payment (the tenant's session, the webhook, an offline payment) nor another
/// reminder of the same installment. The WhatsApp call, like the email queue (FD-13), happens after that change is committed.
/// </summary>
public sealed partial class RentBillingService
{
    public async Task<RentWhatsAppReminderResult> SendWhatsAppReminderAsync(
        Guid leaseId, Guid installmentId, string? note, CancellationToken cancellationToken = default)
    {
        var cleanNote = NormalizeReminderNote(note);
        var intervalHours = RentCharges.GetReminderIntervalHours(configuration);
        var lease = await LoadLeaseAsync(leaseId, cancellationToken);

        ReminderClaim claim;
        string property;
        string due;
        string amount;
        await using (var transaction = await LockLeaseAsync(leaseId, cancellationToken))
        {
            var entry = await FindEntryAsync(leaseId, installmentId, cancellationToken);
            var schedule = await db.RentSchedules.AsNoTracking().SingleAsync(s => s.Id == entry.RentScheduleId, cancellationToken);
            EnsurePayable(entry, schedule);

            var now = TruncateToMicroseconds(UtcNow);
            if (entry.LastReminderAt is { } last && now < last.AddHours(intervalHours))
                throw new DomainRuleException(RentRegisterErrorCodes.ReminderTooSoon, "RentReminderTooSoon", intervalHours);

            claim = new ReminderClaim(now, entry.LastReminderAt, entry.ReminderCount, entry.PaymentTokenHash, entry.PaymentRequestedAt);
            entry.LastReminderAt = now;
            entry.ReminderCount++;
            entry.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            claim = claim with { ReminderCount = entry.ReminderCount };
            property = lease.Property?.Name ?? string.Empty;
            due = entry.DueDate.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture);
            amount = entry.AmountDue.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("it-IT"));
        }

        var settings = whatsAppOptions?.Value ?? new Casazen.Core.Options.WhatsAppOptions();
        var sent = 0;
        try
        {
            foreach (var tenant in WhatsAppTenants(lease))
            {
                try
                {
                    var result = whatsAppSender is null
                        ? new WhatsAppSendResult(false)
                        : await whatsAppSender.SendTemplateAsync(
                            tenant.ContactPhone!,
                            settings.RentReminderTemplate,
                            settings.LanguageCode,
                            [new(FullName(tenant)), new(amount), new(property), new(due), new(cleanNote ?? "-")],
                            cancellationToken);
                    if (result.Sent)
                        sent++;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "WhatsApp rent reminder of installment {InstallmentId} was not accepted ({ExceptionType})",
                        installmentId,
                        ex.GetType().Name);
                }
            }
        }
        catch
        {
            if (sent == 0)
                await RestoreReminderAsync(leaseId, installmentId, claim, CancellationToken.None);
            throw;
        }

        if (sent == 0)
        {
            await RestoreReminderAsync(leaseId, installmentId, claim, cancellationToken);
            throw new DomainRuleException(RentRegisterErrorCodes.ReminderNotSent, "RentWhatsAppReminderNotSent");
        }

        logger.LogInformation("WhatsApp rent reminder of installment {InstallmentId} accepted for {Recipients} tenants", installmentId, sent);
        return new RentWhatsAppReminderResult(installmentId, claim.SentAt, sent, claim.SentAt.AddHours(intervalHours));
    }

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
            var entry = await FindEntryAsync(leaseId, installmentId, cancellationToken);
            var schedule = await db.RentSchedules.AsNoTracking().SingleAsync(s => s.Id == entry.RentScheduleId, cancellationToken);
            EnsurePayable(entry, schedule);

            if (Tenants(lease).Count == 0)
                throw new DomainRuleException(RentBillingErrorCodes.NoTenantEmail, "RentNoTenantEmail");

            var now = TruncateToMicroseconds(UtcNow);
            if (entry.LastReminderAt is { } last && now < last.AddHours(intervalHours))
            {
                throw new DomainRuleException(
                    RentRegisterErrorCodes.ReminderTooSoon, "RentReminderTooSoon", intervalHours);
            }

            claim = new ReminderClaim(now, entry.LastReminderAt, entry.ReminderCount, entry.PaymentTokenHash, entry.PaymentRequestedAt);

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
            await RestoreReminderAsync(leaseId, installmentId, claim, cancellationToken);
            throw new DomainRuleException(RentRegisterErrorCodes.ReminderNotSent, "RentReminderNotSent");
        }

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

    private static string? NormalizeReminderNote(string? note)
    {
        var text = note?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > RentCharges.MaxReminderNoteLength)
            throw new DomainRuleException(RentRegisterErrorCodes.ReminderNoteInvalid, "RentReminderNoteTooLong");

        return text;
    }

    private static List<Party> WhatsAppTenants(LeaseContract lease) =>
        lease.Parties
            .Where(p => p.Role == PartyRole.Tenant && p.AnonymizedAt == null && !string.IsNullOrWhiteSpace(p.ContactPhone))
            .OrderBy(p => p.Position)
            .DistinctBy(p => p.ContactPhone!.Trim(), StringComparer.Ordinal)
            .ToList();

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

    private sealed record ReminderClaim(
        DateTime SentAt,
        DateTime? PreviousReminderAt,
        int PreviousReminderCount,
        string? PreviousTokenHash,
        DateTime? PreviousRequestedAt)
    {
        public int ReminderCount { get; init; }

        public string? NewTokenHash { get; init; }
    }

    private sealed record InstallmentFacts(DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueDate, decimal Amount, bool Overdue);
}
