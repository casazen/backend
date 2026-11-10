using System.Globalization;
using System.Net;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

public sealed record ManualCondominiumDue(Guid Id, Guid CondominiumId, Guid PeriodId, string Name,
    string Label, DateOnly DueDate, decimal Amount, bool Paid);

/// <summary>Reminders only. Never calls Stripe or initiates a bank payment.</summary>
public sealed class CondominiumPaymentReminderService(AppDbContext db, ICondominiumAccountingService accounting,
    IEmailQueue emails, IConfiguration config, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    public async Task<IReadOnlyList<ManualCondominiumDue>> ListAsync(HostScope scope, CancellationToken ct)
    {
        var visible = db.CondominiumUnits.Where(u => u.OrgId == scope.OrgId).Select(u => u.Property).InScope(scope).Select(p => p.Id);
        var rows = await db.CondominiumInstallments.AsNoTracking().Where(i => i.OrgId == scope.OrgId
            && i.Status != CondominiumInstallmentStatus.Cancelled
            && (scope.IsOrgWide || i.Period.Condominium.Units.Any(u => visible.Contains(u.PropertyId))))
            .Select(i => new
            {
                i.Id,
                i.Period.CondominiumId,
                i.CondominiumPeriodId,
                i.Period.Condominium.Name,
                i.Label,
                i.DueDate,
                i.TotalAmount,
                Paid = i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m
            })
            .OrderBy(i => i.DueDate).ToListAsync(ct);
        return rows.Select(i => new ManualCondominiumDue(i.Id, i.CondominiumId, i.CondominiumPeriodId, i.Name,
            i.Label, i.DueDate, Math.Max(0m, i.TotalAmount - i.Paid), i.Paid >= i.TotalAmount)).ToList();
    }
    public async Task MarkPaidAsync(HostScope scope, Guid installmentId, string userId, CancellationToken ct)
    {
        var row = (await ListAsync(scope, ct)).SingleOrDefault(i => i.Id == installmentId) ?? throw new NotFoundException("Installment not found");
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.Condominium, row.CondominiumId.ToString("N")));
        // Read the residual again under the accounting lock. A double click records no second payment.
        var current = (await ListAsync(scope, ct)).Single(i => i.Id == installmentId);
        if (!current.Paid && current.Amount > 0)
            await accounting.RecordPaymentAsync(scope, row.CondominiumId, row.PeriodId,
                new(installmentId, false, current.Amount, clock.TodayInRomeAsDateOnly(), "Bonifico home banking",
                    null, $"manual-condominium:{installmentId:N}", "Pagamento dichiarato dal locatore"), userId, ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }
    public async Task RunAsync(CancellationToken ct)
    {
        var today = clock.TodayInRomeAsDateOnly();
        var until = today.AddDays(5);
        var recent = today.AddDays(-30);
        var candidates = await db.CondominiumInstallments.AsNoTracking().Where(i => i.DueDate <= until && i.DueDate >= recent
            && i.ReminderSentAt == null && i.Status != CondominiumInstallmentStatus.Paid && i.Status != CondominiumInstallmentStatus.Cancelled)
            .Select(i => new { i.Id, i.Period.CondominiumId }).ToListAsync(ct);
        foreach (var c in candidates)
        {
            await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
                (PostgresAdvisoryLocks.Scope.Condominium, c.CondominiumId.ToString("N")));
            var row = await db.CondominiumInstallments.Include(i => i.Org).Include(i => i.Period).ThenInclude(p => p.Condominium)
                .SingleAsync(i => i.Id == c.Id, ct);
            await db.Entry(row).ReloadAsync(ct);
            var paid = await db.CondominiumPayments.Where(p => p.CondominiumInstallmentId == row.Id).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
            if (row.ReminderSentAt is not null || row.Status is CondominiumInstallmentStatus.Paid or CondominiumInstallmentStatus.Cancelled
                || paid >= row.TotalAmount) continue;
            var url = (config["App:PublicSiteBaseUrl"] ?? "").TrimEnd('/') + "/app/long-rent/payments";
            var body = $"<p>Spese condominiali: {WebUtility.HtmlEncode(row.Period.Condominium.Name)}</p>"
                + $"<p>Scadenza: {row.DueDate:dd/MM/yyyy}. Da pagare: {(row.TotalAmount - paid).ToString("F2", CultureInfo.GetCultureInfo("it-IT"))} €.</p>"
                + "<p>Effettua il bonifico dal tuo home banking. CasaZen non avvia il pagamento.</p>"
                + $"<p><a href=\"{WebUtility.HtmlEncode(url)}\">Apri il riepilogo e segna come pagato</a></p>";
            if (emails.Enqueue(row.Org.ContactEmail, new EmailContent("Promemoria spese condominiali", body), "CondominiumPaymentReminder"))
            { row.ReminderSentAt = clock.GetUtcNow().UtcDateTime; await db.SaveChangesAsync(ct); }
            if (tx is not null) await tx.CommitAsync(ct);
        }
    }
}
