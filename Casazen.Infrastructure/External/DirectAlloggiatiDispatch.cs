using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.External;

public sealed class DirectAlloggiatiDispatch(AppDbContext db, DirectAlloggiatiSoap soap, IAlloggiatiWebService reports,
    IAlloggiatiDelegationService delegation, IDirectAlloggiatiRoute route, TimeProvider clock, IFeatureFlags flags,
    AlloggiatiTransportEnvironment environment)
{
    // The arrival report has already passed the arrival-day scheduler; an actual arrival is required as well.
    public async Task<List<AlloggiatiJobTarget>> CandidatesAsync(CancellationToken ct)
    {
        if (!environment.Live) return [];
        var today = clock.TodayInRomeAsDateOnly();
        var yesterdayStart = RomeCalendar.StartOfDayUtc(today.AddDays(-1));
        var tomorrowStart = RomeCalendar.StartOfDayUtc(today.AddDays(1));
        return await db.AlloggiatiWebReports.AsNoTracking()
            .Where(r => r.Status == AlloggiatiWebStatus.DaInviareManualmente && r.GuestId == r.Booking.GuestId
                && r.Booking.ArrivedAt >= yesterdayStart && r.Booking.ArrivedAt < tomorrowStart && r.Booking.NoShowAt == null
                && r.Booking.ArrivedAt != null && r.Booking.Property.Country == "IT"
                && db.PropertyQuesturaCredentials.Any(c => c.PropertyId == r.Booking.PropertyId && c.LastVerificationOk == true))
            .Select(r => new AlloggiatiJobTarget(r.OrgId, r.Id)).ToListAsync(ct);
    }

    public async Task ProcessAsync(Guid reportId, CancellationToken ct)
    {
        if (!environment.Live) return;
        var report = await db.AlloggiatiWebReports.Include(r => r.Booking).SingleAsync(r => r.Id == reportId, ct);
        var booking = report.Booking;
        if (report.Status != AlloggiatiWebStatus.DaInviareManualmente || report.ManuallyCompleted || report.GuestId != booking.GuestId
            || booking.ArrivedAt == null || !AlloggiatiStatusRules.IsDue(booking) || !await EligibleAsync(booking, ct)) return;

        var account = await db.PropertyQuesturaCredentials.AsNoTracking().SingleOrDefaultAsync(c =>
            c.PropertyId == booking.PropertyId && c.OrgId == booking.OrgId && c.LastVerificationOk == true, ct);
        if (account == null) return;
        var summary = await reports.GetGuestSummaryAsync(booking.Id);
        if (!summary.ExportReady || summary.Guests.Count != booking.NumberOfGuests || summary.Guests.Any(g => !g.StayGuestId.HasValue)) return;
        var actualArrival = RomeCalendar.DateInRome(booking.ArrivedAt.Value);
        if (summary.Guests.Any(g => DateOnly.FromDateTime(g.ArrivalDate) != actualArrival)) return;
        var file = AlloggiatiRecordFile.Build(summary.Guests);
        if (file.Text == null || !file.Success) return;
        var rows = file.Text.Split("\r\n", StringSplitOptions.None);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Text)));
        if (await db.AlloggiatiSendAttempts.AnyAsync(a => a.BookingId == booking.Id && a.Mode == AlloggiatiMode.Live
            && (a.Status != AlloggiatiAttemptStatus.TestRejected || (a.SnapshotHash == hash && a.CredentialVersion == account.CredentialVersion)), ct)) return;

        var token = await soap.TokenAsync(account, ct);
        var tested = await soap.RowsAsync(account, token, rows, false, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var attempt = new AlloggiatiSendAttempt
        {
            OrgId = booking.OrgId, BookingId = booking.Id, PropertyId = booking.PropertyId, AccountKey = account.AccountKey,
            CredentialVersion = account.CredentialVersion, Mode = AlloggiatiMode.Live, SnapshotHash = hash,
            Trigger = AlloggiatiAttemptTriggers.ArrivalDayJob, GuestCount = rows.Length, ReservedAt = now, LeaseExpiresAt = now.AddMinutes(2),
            GuestsJson = JsonSerializer.Serialize(summary.Guests.Select(g => new { stayGuestId = g.StayGuestId, position = g.Position, outcome = "pending" }))
        };
        if (tested.Any(x => !x))
        {
            attempt.Status = AlloggiatiAttemptStatus.TestRejected;
            attempt.ErrorCode = "alloggiati_test_rejected";
            attempt.GuestsJson = Outcomes(summary, tested, "validated");
            attempt.CompletedAt = now;
            db.Add(attempt);
            report.ErrorMessage = attempt.ErrorCode;
            await db.SaveChangesAsync(ct);
            return;
        }

        // Keep the same property lock as cutover and credential changes through the network call.
        await using var ownership = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(db,
            PostgresAdvisoryLocks.Scope.InstitutionalSenderCutover, booking.PropertyId.ToString("N"), ct);
        if (ownership == null) return;
        var currentSummary = await reports.GetGuestSummaryAsync(booking.Id);
        if (!currentSummary.ExportReady || !summary.Guests.Select(g => g.StayGuestId).SequenceEqual(currentSummary.Guests.Select(g => g.StayGuestId)) ||
            !string.Equals(file.Text, AlloggiatiRecordFile.Build(currentSummary.Guests).Text, StringComparison.Ordinal)) return;
        // Claim the current report in the database before the irreversible call. A crashed worker
        // leaves an open attempt that a later worker can only reconcile, never resend.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var claimed = await db.AlloggiatiWebReports.Where(r => r.Id == report.Id && r.Status == AlloggiatiWebStatus.DaInviareManualmente
                && !r.ManuallyCompleted && r.Booking.NoShowAt == null && r.Booking.ArrivedAt == booking.ArrivedAt && r.Booking.CheckInDate == booking.CheckInDate && r.GuestId == r.Booking.GuestId
                && r.Booking.PropertyId == account.PropertyId && r.Booking.Property.Country == "IT" && r.Booking.NumberOfGuests == rows.Length
                && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(r.Booking.Status)
                && db.PropertyQuesturaCredentials.Any(c => c.Id == account.Id && c.CredentialVersion == account.CredentialVersion && c.UpdatedAt == account.UpdatedAt && c.LastVerificationOk == true))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, AlloggiatiWebStatus.Sending), ct);
            if (claimed != 1 || !await EligibleAsync(booking, ct)) return;
            attempt.Status = AlloggiatiAttemptStatus.Sending;
            db.Add(attempt);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        try
        {
            var results = await soap.RowsAsync(account, token, rows, true, ct);
            var accepted = results.Count(x => x);
            (attempt.Status, report.Status) = accepted == rows.Length
                ? (AlloggiatiAttemptStatus.Accepted, AlloggiatiWebStatus.SentReceiptPending)
                : accepted == 0 ? (AlloggiatiAttemptStatus.Rejected, AlloggiatiWebStatus.Rifiutato)
                : (AlloggiatiAttemptStatus.PartiallyAccepted, AlloggiatiWebStatus.PartiallyAccepted);
            attempt.GuestsJson = Outcomes(summary, results, "accepted");
            attempt.ErrorCode = accepted == rows.Length ? null : "alloggiati_rows_rejected";
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Xml.XmlException or InvalidDataException)
        {
            attempt.Status = AlloggiatiAttemptStatus.ToVerify;
            report.Status = AlloggiatiWebStatus.ToVerify;
            attempt.ErrorCode = "alloggiati_acquisition_unknown";
        }
        attempt.CompletedAt = clock.GetUtcNow().UtcDateTime;
        report.UpdatedAt = attempt.CompletedAt.Value;
        report.ErrorMessage = attempt.ErrorCode;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<bool> EligibleAsync(Booking booking, CancellationToken ct)
    {
        // Previously entrusted or ambiguous Octorate writes must be reconciled, even if the new owner is now CasaZen.
        if (await db.StayGuestOctorateLinks.AnyAsync(l => l.OrgId == booking.OrgId && l.BookingId == booking.Id
            && !l.SentAsDraft, ct)) return false;
        if (booking.ArrivedAt is not { } arrival) return false;
        var date = RomeCalendar.DateInRome(arrival);
        if (date != RomeCalendar.DateInRome(booking.CheckInDate)) return false;
        var today = clock.TodayInRomeAsDateOnly();
        var from = await route.EffectiveFromAsync(booking.OrgId, booking.PropertyId, ct);
        if (from == null || date < from.Value || date > today || date < today.AddDays(-1) ||
            !(await delegation.GetAsync(booking.OrgId, ct)).Valid) return false;
        if (!flags.IsEnabled(FeatureFlags.CheckInIdentityVerification)) return true;
        return await db.BiometricVerificationLogs.Where(l => l.OrgId == booking.OrgId && l.BookingId == booking.Id)
            .OrderByDescending(l => l.CreatedAt).Select(l => (BiometricOutcome?)l.Outcome).FirstOrDefaultAsync(ct) == BiometricOutcome.Accept;
    }

    private static string Outcomes(AlloggiatiGuestSummaryInfo summary, bool[] results, string positive) =>
        JsonSerializer.Serialize(summary.Guests.Select((g, i) => new { stayGuestId = g.StayGuestId, position = g.Position,
            outcome = results[i] ? positive : "rejected" }));
}

public sealed record AlloggiatiJobTarget(Guid OrgId, Guid Id);
