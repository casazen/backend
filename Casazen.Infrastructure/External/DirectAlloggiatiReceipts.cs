using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.External;

public sealed class DirectAlloggiatiReceipts(AppDbContext db, DirectAlloggiatiSoap soap, IFileStorage storage,
    AlloggiatiTransportEnvironment environment, TimeProvider clock)
{
    public async Task<List<AlloggiatiJobTarget>> PendingAsync(CancellationToken ct)
    {
        if (!environment.Live) return [];
        var now = clock.GetUtcNow().UtcDateTime;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var expired = db.AlloggiatiSendAttempts.Where(a => a.Mode == AlloggiatiMode.Live &&
                a.Status == AlloggiatiAttemptStatus.Sending && a.LeaseExpiresAt < now);
            await db.AlloggiatiWebReports.Where(r => r.Status == AlloggiatiWebStatus.Sending && expired.Any(a => a.BookingId == r.BookingId))
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, AlloggiatiWebStatus.ToVerify)
                    .SetProperty(r => r.ErrorMessage, "alloggiati_acquisition_unknown"), ct);
            await expired.ExecuteUpdateAsync(set => set.SetProperty(a => a.Status, AlloggiatiAttemptStatus.ToVerify)
                .SetProperty(a => a.ErrorCode, "alloggiati_acquisition_unknown"), ct);
            await tx.CommitAsync(ct);
        }
        return await db.AlloggiatiSendAttempts.AsNoTracking().Where(a => a.Mode == AlloggiatiMode.Live
            && (a.Status == AlloggiatiAttemptStatus.Accepted || a.Status == AlloggiatiAttemptStatus.PartiallyAccepted)
            && a.ReceiptId == null).Select(a => new AlloggiatiJobTarget(a.OrgId, a.Id)).ToListAsync(ct);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        if (!environment.Live) return;
        var attempt = await db.AlloggiatiSendAttempts.SingleAsync(a => a.Id == id, ct);
        if (attempt.ReceiptId.HasValue || attempt.CompletedAt == null || attempt.Mode != AlloggiatiMode.Live ||
            attempt.Status is not (AlloggiatiAttemptStatus.Accepted or AlloggiatiAttemptStatus.PartiallyAccepted)) return;
        var day = RomeCalendar.DateInRome(attempt.ReservedAt);
        var today = clock.TodayInRomeAsDateOnly();
        if (day != RomeCalendar.DateInRome(attempt.CompletedAt.Value) || day >= today || day < today.AddDays(-30)) return;
        var date = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var receipt = await db.AlloggiatiReceipts.SingleOrDefaultAsync(r => r.OrgId == attempt.OrgId && r.AccountKey == attempt.AccountKey
            && r.Mode == AlloggiatiMode.Live && r.ReceiptDate == date, ct);
        if (receipt == null)
        {
            var account = await db.PropertyQuesturaCredentials.AsNoTracking()
                .FirstOrDefaultAsync(c => c.OrgId == attempt.OrgId && c.AccountKey == attempt.AccountKey, ct);
            if (account == null) return;
            var token = await soap.TokenAsync(account, ct);
            var bytes = await soap.ReceiptAsync(account.Username, token, day, ct);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var key = $"alloggiati/{attempt.OrgId:N}/{attempt.AccountKey:N}/{day:yyyy-MM-dd}/{hash}.pdf";
            using var stream = new MemoryStream(bytes, writable: false);
            await storage.PutAsync(StorageBucket.Private, key, stream, "application/pdf", ct);
            var now = clock.GetUtcNow().UtcDateTime;
            receipt = new AlloggiatiReceipt
            {
                OrgId = attempt.OrgId,
                AccountKey = attempt.AccountKey,
                ReceiptDate = date,
                Mode = AlloggiatiMode.Live,
                StorageKey = key,
                ContentHash = hash,
                SizeBytes = bytes.Length,
                RetrievedAt = now,
                RetainUntil = now.AddYears(AlloggiatiReceipt.RetentionYears)
            };
            db.Add(receipt);
        }
        attempt.ReceiptId = receipt.Id;
        if (attempt.Status == AlloggiatiAttemptStatus.Accepted)
        {
            var report = await db.AlloggiatiWebReports.SingleOrDefaultAsync(r => r.OrgId == attempt.OrgId && r.BookingId == attempt.BookingId
                && r.Status == AlloggiatiWebStatus.SentReceiptPending, ct);
            if (report != null)
            {
                report.Status = AlloggiatiWebStatus.Inviato;
                report.ReceiptId = receipt.Id;
                report.ConfirmationNumber = receipt.ContentHash;
                report.ReportedAt = attempt.CompletedAt;
                report.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
