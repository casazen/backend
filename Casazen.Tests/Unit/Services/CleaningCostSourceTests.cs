using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E24/F01/US01/T03 (NF-120): the one read of the cleanings completed with their cost, for the invoicing of the owners. On the world of <see cref="HostScopeScenario"/> (an org with two
/// properties, a second org with one, a supplier org): requests of category cleaning in every status and staff activities written straight to the tables. October 2026 is the period
/// unless a test says another.
/// </summary>
public class CleaningCostSourceTests
{
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);

    private static DateTime Utc(int month, int day, int hour = 10, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static ServiceRequest Request(HostScopeWorld world, Property property, ServiceRequestStatus status, DateTime? completedAt, Action<ServiceRequest>? change = null)
    {
        var request = new ServiceRequest
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            SupplierOrgId = world.SupplierOrgId,
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = ServiceCategories.Cleaning,
            Status = status,
            CompletedAt = completedAt,
            CreatedAt = Utc(9, 1),
            UpdatedAt = Utc(9, 1),
        };
        change?.Invoke(request);
        return request;
    }

    private static InternalActivity StaffWork(HostScopeWorld world, Property property, string status, DateTime? completedAt, decimal? rate, bool recharge, string type = InternalActivity.CleaningActivityType) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        StaffUserId = world.OwnerUserId,
        ActivityType = type,
        Status = status,
        ScheduledAt = Utc(10, 5),
        CompletedAt = completedAt,
        InternalRate = rate,
        RechargeToOwner = recharge,
    };

    private static async Task<(AppDbContext Db, HostScopeWorld World, CleaningCostSource Source)> WorldAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        return (db, world, new CleaningCostSource(db));
    }

    private static async Task SaveAsync(AppDbContext db, params object[] rows)
    {
        db.AddRange(rows);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ─── Proofs: the rules of the brief that cost money or permission if wrong ──────────────────────────────────────────────────

    /// <summary>
    /// Proof (money): only the requests completed or paid are a cost, each once: a request that is cancelled, refused or still open (new, taken, in progress) never comes back, nor does a
    /// backup that waits, a request of a supplier's showcase or one of another category.
    /// </summary>
    [Fact]
    public async Task CleaningCost_CancelledAndRefusedExcluded_Test()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.PropertyId == world.Granted.Id);
        var completed = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 5), r =>
        {
            r.BookingId = booking.Id;
            r.FinalAmountCents = 8000;
        });
        var paid = Request(world, world.Granted, ServiceRequestStatus.Pagato, Utc(10, 6), r =>
        {
            r.FinalAmountCents = 9000;
            r.PaidAt = Utc(10, 20);
        });
        await SaveAsync(
            db,
            completed,
            paid,
            Request(world, world.Granted, ServiceRequestStatus.Annullato, Utc(10, 7), r => r.FinalAmountCents = 1000),
            Request(world, world.Granted, ServiceRequestStatus.Rifiutato, null, r => r.FinalAmountCents = 1000),
            Request(world, world.Granted, ServiceRequestStatus.Richiesto, null, r => r.EstimatedAmountCents = 1000),
            Request(world, world.Granted, ServiceRequestStatus.PresoInCarico, null, r => r.QuotedAmountCents = 1000),
            Request(world, world.Granted, ServiceRequestStatus.InCorso, null, r => r.QuotedAmountCents = 1000),
            Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 8), r =>
            {
                r.FinalAmountCents = 1000;
                r.GroupRole = ServiceRequestGroupRole.Backup;
            }),
            Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 9), r =>
            {
                r.FinalAmountCents = 1000;
                r.Category = ServiceCategories.Cleaning + "-extra";
            }),
            Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 9), r =>
            {
                r.FinalAmountCents = 1000;
                r.RentalContext = ServiceRequestRentalContext.Showcase;
            }));

        var cleanings = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);

        Assert.Equal([completed.Id, paid.Id], cleanings.Select(c => c.SourceId));
        var first = cleanings[0];
        Assert.Equal(
            (world.Granted.Id, (Guid?)booking.Id, new DateOnly(2026, 10, 5), 8000L, "EUR", CleaningAmountVat.IncludesSupplierVat, CleaningKind.Supplier, true, CleaningSourceKind.ServiceRequest),
            (first.PropertyId, first.BookingId, first.CompletedOn, first.AmountCents, first.Currency, first.AmountVat, first.Kind, first.RechargeToOwner, first.SourceKind));
        Assert.Equal(9000L, cleanings[1].AmountCents);
        Assert.All(cleanings, c => Assert.Null(c.SupplierInvoiceReference));
    }

    /// <summary>
    /// Proof (permission): a property id of another organization returns nothing, whoever asks and whatever else is in the list; with no list the answer is the org's own properties only,
    /// and the other org reads only its own.
    /// </summary>
    [Fact]
    public async Task CleaningCost_OtherOrganizationPropertyEmpty_Test()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        var own = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 5), r => r.FinalAmountCents = 8000);
        var foreign = Request(world, world.OtherOrgProperty, ServiceRequestStatus.Completato, Utc(10, 5), r => r.FinalAmountCents = 7000);
        await SaveAsync(db, own, foreign, StaffWork(world, world.OtherOrgProperty, InternalActivity.StatusCompleted, Utc(10, 5), 30m, true));

        var asked = await source.GetCompletedAsync(world.OrgId, [world.OtherOrgProperty.Id], From, To, CancellationToken.None);
        var mixed = await source.GetCompletedAsync(world.OrgId, [world.OtherOrgProperty.Id, world.Granted.Id], From, To, CancellationToken.None);
        var all = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);
        var theirs = await source.GetCompletedAsync(world.OtherOrgId, null, From, To, CancellationToken.None);
        var unknown = await source.GetCompletedAsync(Guid.NewGuid(), null, From, To, CancellationToken.None);

        Assert.Empty(asked);
        Assert.Equal([own.Id], mixed.Select(c => c.SourceId));
        Assert.Equal([own.Id], all.Select(c => c.SourceId));
        Assert.Equal(2, theirs.Count);
        Assert.All(theirs, c => Assert.Equal(world.OtherOrgProperty.Id, c.PropertyId));
        Assert.Empty(unknown);
    }

    /// <summary>
    /// Proof (money): a staff cost is the internal rate of a completed Cleaning activity, in cents, with its recharge flag, and its VAT is not applicable (a supplier's VAT is not in it); an
    /// activity not completed, cancelled, of another type or a «To do» of the same table never comes back.
    /// </summary>
    [Fact]
    public async Task CleaningCost_StaffVatNotApplicable_Test()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.PropertyId == world.Granted.Id);
        var charged = StaffWork(world, world.Granted, InternalActivity.StatusCompleted, Utc(10, 5), 40.50m, recharge: true);
        charged.BookingId = booking.Id;
        var free = StaffWork(world, world.Granted, InternalActivity.StatusCompleted, Utc(10, 6), null, recharge: false);
        var todo = StaffWork(world, world.Granted, InternalActivity.StatusCompleted, Utc(10, 7), 99m, recharge: true);
        todo.Action = ActivityAction.ReviewBooking;
        await SaveAsync(
            db,
            charged,
            free,
            todo,
            StaffWork(world, world.Granted, InternalActivity.StatusPending, null, 40m, true),
            StaffWork(world, world.Granted, InternalActivity.StatusInProgress, null, 40m, true),
            StaffWork(world, world.Granted, InternalActivity.StatusCancelled, null, 40m, true),
            StaffWork(world, world.Granted, InternalActivity.StatusCompleted, Utc(10, 8), 40m, true, type: "Maintenance"));

        var cleanings = await source.GetCompletedAsync(world.OrgId, [world.Granted.Id], From, To, CancellationToken.None);

        Assert.Equal([charged.Id, free.Id], cleanings.Select(c => c.SourceId));
        Assert.Equal(
            (CleaningKind.Staff, CleaningSourceKind.InternalActivity, CleaningAmountVat.NotApplicable, 4050L, true, (Guid?)booking.Id),
            (cleanings[0].Kind, cleanings[0].SourceKind, cleanings[0].AmountVat, cleanings[0].AmountCents, cleanings[0].RechargeToOwner, cleanings[0].BookingId));
        Assert.Equal((CleaningAmountVat.NotApplicable, 0L, false), (cleanings[1].AmountVat, cleanings[1].AmountCents, cleanings[1].RechargeToOwner));
        Assert.All(cleanings, c => Assert.Equal("EUR", c.Currency));
    }

    /// <summary>
    /// Proof (money): a final amount above the quote by more than the tolerance (decision D7) that the host has not confirmed is not a cost, in no period. One confirmed late comes back once, in
    /// the period of its confirmation, with the day of the confirmation as <c>CompletedOn</c> (the very date the period is judged on) and not in the earlier period that may be invoiced already;
    /// one that never needed the confirmation keeps the day of its completion, and one the host paid by hand with the confirmation still open counts from the day it was paid.
    /// </summary>
    [Fact]
    public async Task CleaningCost_UnconfirmedAmountIsNoCost_ConfirmedLateFallsInThePeriodOfTheConfirmation_Test()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        var waiting = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(9, 29), r =>
        {
            r.FinalAmountCents = 9000;
            r.QuotedAmountCents = 6000;
            r.FinalAmountNeedsConfirmation = true;
        });
        var late = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(9, 29), r =>
        {
            r.FinalAmountCents = 9500;
            r.QuotedAmountCents = 6000;
            r.FinalAmountConfirmedAt = Utc(10, 8);
        });
        var plain = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 5), r => r.FinalAmountCents = 6000);
        var paidByHand = Request(world, world.Granted, ServiceRequestStatus.Pagato, Utc(9, 28), r =>
        {
            r.FinalAmountCents = 9800;
            r.QuotedAmountCents = 6000;
            r.FinalAmountNeedsConfirmation = true;
            r.PaidAt = Utc(10, 20);
        });
        await SaveAsync(db, waiting, late, plain, paidByHand);

        var september = await source.GetCompletedAsync(world.OrgId, null, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);
        var october = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);

        Assert.Empty(september);
        Assert.Equal([plain.Id, late.Id, paidByHand.Id], october.Select(c => c.SourceId));
        Assert.Equal([new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 20)], october.Select(c => c.CompletedOn));
        Assert.Equal([6000L, 9500L, 9800L], october.Select(c => c.AmountCents));
        Assert.DoesNotContain(waiting.Id, october.Select(c => c.SourceId));

        // The host confirms the amount that waited: from then on it is a cost, once, on the day of the confirmation.
        db.ChangeTracker.Clear();
        var confirmed = await db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == waiting.Id);
        confirmed.FinalAmountNeedsConfirmation = false;
        confirmed.FinalAmountConfirmedAt = Utc(11, 3);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var septemberAfter = await source.GetCompletedAsync(world.OrgId, null, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);
        var octoberAfter = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);
        var november = await source.GetCompletedAsync(world.OrgId, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), CancellationToken.None);

        Assert.Empty(septemberAfter);
        Assert.Equal(october.Select(c => c.SourceId), octoberAfter.Select(c => c.SourceId));
        var entry = Assert.Single(november);
        Assert.Equal((waiting.Id, new DateOnly(2026, 11, 3), 9000L), (entry.SourceId, entry.CompletedOn, entry.AmountCents));
    }

    // ─── The amount ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The amount is the final one the supplier declared, else the price it committed to, else a quote still waiting, else the estimate; none at all is 0, and the VAT says it does not know.</summary>
    [Fact]
    public async Task Amount_IsTheFinalOne_ThenTheCommittedOne_ThenTheQuote_ThenTheEstimate_AndNeverMadeUp()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        ServiceRequest Done(int day, Action<ServiceRequest> change) => Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, day), change);
        var rows = new[]
        {
            Done(1, r => { r.FinalAmountCents = 5000; r.QuotedAmountCents = 4000; r.QuoteTotalCents = 3000; r.EstimatedAmountCents = 2000; }),
            Done(2, r => { r.QuotedAmountCents = 4000; r.QuoteTotalCents = 3000; r.EstimatedAmountCents = 2000; }),
            Done(3, r => { r.QuoteTotalCents = 3000; r.EstimatedAmountCents = 2000; }),
            Done(4, r => r.EstimatedAmountCents = 2000),
            Done(5, _ => { }),
        };
        await SaveAsync(db, [.. rows]);

        var cleanings = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);

        Assert.Equal([5000L, 4000L, 3000L, 2000L, 0L], cleanings.Select(c => c.AmountCents));
        Assert.Equal(
            [CleaningAmountVat.IncludesSupplierVat, CleaningAmountVat.IncludesSupplierVat, CleaningAmountVat.IncludesSupplierVat, CleaningAmountVat.IncludesSupplierVat, CleaningAmountVat.Unknown],
            cleanings.Select(c => c.AmountVat));
    }

    // ─── The period, the properties ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The days are Europe/Rome days, both ends included: a cleaning completed at 23:30 UTC on 31 October is 1 November in Rome (winter time).</summary>
    [Fact]
    public async Task Period_IsRomeDaysBothEndsIncluded()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        var lastMinuteOfOctober = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 31, 22, 59), r => r.FinalAmountCents = 100);
        var firstMinuteOfNovember = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 31, 23, 0), r => r.FinalAmountCents = 200);
        var firstOfOctober = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(9, 30, 22, 0), r => r.FinalAmountCents = 300);
        var beforeOctober = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(9, 30, 21, 59), r => r.FinalAmountCents = 400);
        await SaveAsync(db, lastMinuteOfOctober, firstMinuteOfNovember, firstOfOctober, beforeOctober);

        var october = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);
        var november = await source.GetCompletedAsync(world.OrgId, null, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), CancellationToken.None);
        var oneDay = await source.GetCompletedAsync(world.OrgId, null, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1), CancellationToken.None);
        var backwards = await source.GetCompletedAsync(world.OrgId, null, To, From, CancellationToken.None);

        Assert.Equal([firstOfOctober.Id, lastMinuteOfOctober.Id], october.Select(c => c.SourceId));
        Assert.Equal([firstMinuteOfNovember.Id], november.Select(c => c.SourceId));
        Assert.Equal(new DateOnly(2026, 11, 1), november[0].CompletedOn);
        Assert.Equal([firstOfOctober.Id], oneDay.Select(c => c.SourceId));
        Assert.Empty(backwards);
    }

    /// <summary>An empty list of properties is an empty answer, never «every property»; a list narrows the answer, and a property deleted since is still the owner's cost, in its own currency.</summary>
    [Fact]
    public async Task Properties_AnEmptyListIsNone_AListNarrows_AndADeletedPropertyStillCounts()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        var granted = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 5), r => r.FinalAmountCents = 1000);
        var hidden = Request(world, world.Hidden, ServiceRequestStatus.Completato, Utc(10, 6), r => r.FinalAmountCents = 2000);
        await SaveAsync(db, granted, hidden);
        db.ChangeTracker.Clear();
        var property = await db.Properties.SingleAsync(p => p.Id == world.Hidden.Id);
        property.IsDeleted = true;
        property.DeletedAt = Utc(10, 20);
        property.Currency = "CHF";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var none = await source.GetCompletedAsync(world.OrgId, [], From, To, CancellationToken.None);
        var onlyGranted = await source.GetCompletedAsync(world.OrgId, [world.Granted.Id], From, To, CancellationToken.None);
        var all = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);

        Assert.Empty(none);
        Assert.Equal([granted.Id], onlyGranted.Select(c => c.SourceId));
        Assert.Equal([granted.Id, hidden.Id], all.Select(c => c.SourceId));
        Assert.Equal(["EUR", "CHF"], all.Select(c => c.Currency));
    }

    /// <summary>Supplier requests and staff activities are one list, oldest first.</summary>
    [Fact]
    public async Task TheTwoSources_AreOneListOldestFirst()
    {
        var (db, world, source) = await WorldAsync();
        await using var disposable = db;
        var request = Request(world, world.Granted, ServiceRequestStatus.Completato, Utc(10, 9), r => r.FinalAmountCents = 1000);
        var activity = StaffWork(world, world.Granted, InternalActivity.StatusCompleted, Utc(10, 3), 20m, true);
        await SaveAsync(db, request, activity);

        var cleanings = await source.GetCompletedAsync(world.OrgId, null, From, To, CancellationToken.None);

        Assert.Equal([activity.Id, request.Id], cleanings.Select(c => c.SourceId));
        Assert.Equal([CleaningKind.Staff, CleaningKind.Supplier], cleanings.Select(c => c.Kind));
    }
}
