using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Owners;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E14/F01/US02/T01 (NF-114, NF-115, ⚖ I13): with the mandate on the actual cost every cleaning completed in the month and recharged to the owner is one charge linked to
/// its request, read only through the cost source of the cleanings, in place of the flat fee of the stay (never both); a cancelled request posts nothing; a staff cleaning with
/// no price costs the flat rate of the mandate and one with no price at all holds the statement back; a VAT the source does not say raises a «Da fare» and is never guessed; the
/// owner invoice shows one recharge line for each request with its reference; the mode is a field of the mandate that defaults to the flat rate.
/// </summary>
public class CleaningRechargeTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly HostScope OrgWide = new(OrgId);

    private static DateTime Day(int month, int day) => new(2026, month, day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The instant a cleaning was completed: 9:00 UTC, the same Europe/Rome day.</summary>
    private static DateTime At(int month, int day) => new(2026, month, day, 9, 0, 0, DateTimeKind.Utc);

    private static List<OwnerLedgerEntry> CleaningLines(IEnumerable<OwnerLedgerEntry> lines) =>
        [.. lines.Where(e => e.SourceType == OwnerLedgerSourceType.CleaningRequest).OrderBy(e => e.ServiceDate).ThenBy(e => e.Amount)];

    /// <summary>
    /// PROOF TEST: money rule. With the mandate on the actual cost every completed cleaning request of the month becomes one owner charge linked to the request (its price, the
    /// day it was completed, its reference), once however many times the month is posted, and the flat fee of the same stay is not posted: the cleaning is never charged both ways.
    /// An owner on the flat rate keeps the flat fee of its stay and gets none of the requests of its property. A request whose price changed is a reversal and a new line.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_OncePerRequest_NoFlatFeeTwice_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync(owner, "TEST Casa Lago");
        await world.MandateAsync(owner, CleaningRechargeMode.ActualCost, 40m);
        var stay = await world.StayAsync(property, Day(10, 17), Day(10, 20));
        var linked = await world.RequestAsync(property, ServiceRequestStatus.Completato, At(10, 20), r =>
        {
            r.BookingId = stay;
            r.FinalAmountCents = 8000;
        });
        var loose = await world.RequestAsync(property, ServiceRequestStatus.Pagato, At(10, 25), r =>
        {
            r.QuotedAmountCents = 5000;
            r.PaidAt = At(10, 26);
        });

        var other = await world.OwnerAsync();
        var otherProperty = await world.PropertyAsync(other, "TEST Casa Monte");
        await world.MandateAsync(other, CleaningRechargeMode.FlatRate, 40m);
        var otherStay = await world.StayAsync(otherProperty, Day(10, 18), Day(10, 22));
        await world.RequestAsync(otherProperty, ServiceRequestStatus.Completato, At(10, 22), r =>
        {
            r.BookingId = otherStay;
            r.FinalAmountCents = 9000;
        });

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, "auth0|admin");

        Assert.DoesNotContain(result.Issues, i => i.Blocking);
        var lines = await world.LiveAsync();
        var requests = CleaningLines(lines);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, e => Assert.Equal((OwnerLedgerEntryType.OwnerCharge, owner, "CHF", property), (e.EntryType, e.PropertyOwnerId, e.StatementCurrency, e.PropertyId!.Value)));
        Assert.Equal(
            [(linked, -80m, new DateOnly(2026, 10, 20)), (loose, -50m, new DateOnly(2026, 10, 25))],
            requests.Select(e => (e.SourceId!.Value, e.Amount, e.RecognitionDate)));
        Assert.Equal((OwnerLedgerCleaningRecharges.ReferenceOf(linked), "Pulizia del 20/10/2026"), (requests[0].SourceReference, requests[0].Description));

        // The flat fee of the stay is not posted for the owner on the actual cost: never both. The owner on the flat rate keeps it, and gets no cleaning request.
        Assert.DoesNotContain(lines, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee && e.PropertyOwnerId == owner);
        var flat = Assert.Single(lines, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee && e.PropertyOwnerId == other);
        Assert.Equal((otherStay, -40m), (flat.SourceId!.Value, flat.Amount));
        Assert.DoesNotContain(requests, e => e.PropertyOwnerId == other);

        // Posting again changes nothing.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((0, 0), (again.Posted, again.Reversed));
        Assert.Equal(2, CleaningLines(await world.LiveAsync()).Count);

        // The price of a request changes after it was posted: the old line is reversed and one new line stands for the request.
        await world.ChangeRequestAsync(linked, r => r.FinalAmountCents = 9000);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var changed = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((1, 1), (changed.Posted, changed.Reversed));
        var live = CleaningLines(await world.LiveAsync());
        Assert.Equal(2, live.Count);
        Assert.Equal(-90m, Assert.Single(live, e => e.SourceId == linked).Amount);
        Assert.DoesNotContain(await world.LiveAsync(), e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee && e.PropertyOwnerId == owner);
    }

    /// <summary>
    /// PROOF TEST: money rule. A cancelled or refused request is never a cost, so it posts nothing; a request posted and cancelled afterwards is no longer read, so its line is
    /// reversed and the owner owes nothing for it.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_CancelledRequestPostsNothing_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync(owner, "TEST Casa Lago");
        await world.MandateAsync(owner, CleaningRechargeMode.ActualCost, null);
        var done = await world.RequestAsync(property, ServiceRequestStatus.Completato, At(10, 5), r => r.FinalAmountCents = 6000);
        await world.RequestAsync(property, ServiceRequestStatus.Annullato, At(10, 6), r => r.FinalAmountCents = 7000);
        await world.RequestAsync(property, ServiceRequestStatus.Rifiutato, null, r => r.FinalAmountCents = 7000);

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);

        Assert.Equal(1, result.Posted);
        Assert.Equal([done], CleaningLines(await world.LiveAsync()).Select(e => e.SourceId!.Value));

        await world.ChangeRequestAsync(done, r => r.Status = ServiceRequestStatus.Annullato);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var after = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);

        Assert.Equal((0, 1), (after.Posted, after.Reversed));
        Assert.Empty(await world.LiveAsync());
        Assert.Equal(0m, await world.Db.OwnerLedgerEntries.AsNoTracking().SumAsync(e => e.Amount));
    }

    /// <summary>
    /// PROOF TEST: money rule. The mandate of the stay decides, so a mandate that changes its mode between the check-out and the cleaning never charges the cleaning of a stay
    /// twice, nor none: a stay that checked out under the flat rate keeps its flat fee and its cleaning of the next month posts nothing; one that checked out under the actual cost
    /// has no flat fee and its cleaning of the next month is the charge.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_ModeOfTheStayDecides_NeverBothAcrossAModeChange_Test()
    {
        var world = await World.CreateAsync();
        var toActual = await world.OwnerAsync();
        var fromActual = await world.OwnerAsync();
        var firstProperty = await world.PropertyAsync(toActual, "TEST Casa Lago");
        var secondProperty = await world.PropertyAsync(fromActual, "TEST Casa Monte");
        await world.MandateAsync(toActual, CleaningRechargeMode.FlatRate, 40m, new DateOnly(2026, 1, 1));
        await world.MandateAsync(toActual, CleaningRechargeMode.ActualCost, 40m, new DateOnly(2026, 11, 1));
        await world.MandateAsync(fromActual, CleaningRechargeMode.ActualCost, 40m, new DateOnly(2026, 1, 1));
        await world.MandateAsync(fromActual, CleaningRechargeMode.FlatRate, 40m, new DateOnly(2026, 11, 1));
        var firstStay = await world.StayAsync(firstProperty, Day(10, 28), Day(10, 31));
        var secondStay = await world.StayAsync(secondProperty, Day(10, 28), Day(10, 31));
        var first = await world.RequestAsync(firstProperty, ServiceRequestStatus.Completato, At(11, 1), r =>
        {
            r.BookingId = firstStay;
            r.FinalAmountCents = 8000;
        });
        var second = await world.RequestAsync(secondProperty, ServiceRequestStatus.Completato, At(11, 1), r =>
        {
            r.BookingId = secondStay;
            r.FinalAmountCents = 7000;
        });

        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var october = await world.LiveAsync();
        Assert.Equal(firstStay, Assert.Single(october, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee).SourceId);
        Assert.Equal(toActual, Assert.Single(october, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee).PropertyOwnerId);

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Service.PostPeriodAsync(OrgId, 2026, 11, null);
        var november = CleaningLines(await world.LiveAsync());
        Assert.Equal([second], november.Select(e => e.SourceId!.Value));
        Assert.Equal((fromActual, -70m, 11), (november[0].PropertyOwnerId, november[0].Amount, november[0].PeriodMonth));
        Assert.DoesNotContain(november, e => e.SourceId == first);
    }

    /// <summary>
    /// PROOF TEST: money rule. A staff cleaning with a rate costs the rate; one with none costs the flat rate of the mandate, in the settlement currency of the owner; one with no price
    /// at all is never a charge of zero: the statement is held back (<c>source_amount_missing</c>) until it has one; one the agency does not recharge is nobody's charge.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_StaffCleaningWithoutPriceCostsTheMandateFlatRate_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync(owner, "TEST Casa Lago");
        await world.MandateAsync(owner, CleaningRechargeMode.ActualCost, 40m);
        var priced = await world.StaffAsync(property, At(10, 3), 25.50m, recharge: true);
        var unpriced = await world.StaffAsync(property, At(10, 4), null, recharge: true);
        await world.StaffAsync(property, At(10, 5), 30m, recharge: false);

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);

        Assert.DoesNotContain(result.Issues, i => i.Blocking);
        Assert.Equal([(priced, -25.50m), (unpriced, -40m)], CleaningLines(await world.LiveAsync()).Select(e => (e.SourceId!.Value, e.Amount)));

        // Without a flat rate in the mandate the cleaning has no price at all: no charge of zero, the statement of the owner waits.
        var bare = await world.OwnerAsync();
        var bareProperty = await world.PropertyAsync(bare, "TEST Casa Monte");
        await world.MandateAsync(bare, CleaningRechargeMode.ActualCost, null);
        var nothing = await world.StaffAsync(bareProperty, At(10, 6), null, recharge: true);

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var held = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);

        var issue = Assert.Single(held.Issues, i => i.Code == OwnerReadinessCodes.SourceAmountMissing);
        Assert.Equal((bare, bareProperty, nothing, true), (issue.OwnerId!.Value, issue.PropertyId!.Value, issue.SourceId!.Value, issue.Blocking));
        Assert.DoesNotContain(CleaningLines(await world.LiveAsync()), e => e.PropertyOwnerId == bare);
    }

    /// <summary>
    /// PROOF TEST: money rule. A VAT the cost source does not say is never guessed: the charge is posted as it is and a «Da fare» asks the agency to state the VAT, once for the stay
    /// however often the month is posted; closed by a person it is not raised again, and when the source says the VAT the item the system raised is closed. A cleaning with no amount
    /// at all is not posted as a charge of zero: it holds the statement back and raises the same item.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_UnknownVatPostsTheChargeAndRaisesTheTodoOnce_Test()
    {
        var source = new StubCostSource();
        var world = await World.CreateAsync(source);
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync(owner, "TEST Casa Lago");
        await world.MandateAsync(owner, CleaningRechargeMode.ActualCost, null);
        var stay = await world.StayAsync(property, Day(10, 17), Day(10, 20));
        var request = Guid.NewGuid();
        source.Set(Cleaning(property, stay, request, 7000, CleaningAmountVat.Unknown));

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);

        Assert.DoesNotContain(result.Issues, i => i.Blocking);
        Assert.Equal(-70m, Assert.Single(CleaningLines(await world.LiveAsync())).Amount);
        var todo = Assert.Single(await world.TodosAsync());
        Assert.Equal(nameof(ActivityType.CleaningVatToState), todo.ActivityType);
        Assert.Equal(ActivityAction.ReviewBooking, todo.Action);
        Assert.Equal(InternalActivity.BookingEntityType, todo.EntityType);
        Assert.Equal(stay, todo.EntityId);
        Assert.Equal(property, todo.PropertyId);
        Assert.False(string.IsNullOrWhiteSpace(todo.Reason));
        Assert.Null(todo.CompletedAt);

        // The same month posted again finds the same item.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Single(await world.TodosAsync());

        // The agency closes it by hand: it is not raised again while the VAT is still unknown.
        await world.Activities.CloseAsync(todo.Id, "auth0|admin", CancellationToken.None);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        var closedByAPerson = Assert.Single(await world.TodosAsync());
        Assert.Equal("auth0|admin", closedByAPerson.ClosedBy);

        // A cleaning with no amount at all: no charge of zero, the statement waits, and the stay has an item.
        var bare = await world.OwnerAsync();
        var bareProperty = await world.PropertyAsync(bare, "TEST Casa Monte");
        await world.MandateAsync(bare, CleaningRechargeMode.ActualCost, null);
        var bareStay = await world.StayAsync(bareProperty, Day(10, 17), Day(10, 21));
        var bareRequest = Guid.NewGuid();
        source.Set(Cleaning(property, stay, request, 7000, CleaningAmountVat.Unknown), Cleaning(bareProperty, bareStay, bareRequest, 0, CleaningAmountVat.Unknown));
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var held = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Contains(held.Issues, i => i.Code == OwnerReadinessCodes.SourceAmountMissing && i.OwnerId == bare && i.Blocking);
        Assert.DoesNotContain(CleaningLines(await world.LiveAsync()), e => e.PropertyOwnerId == bare);
        var bareTodo = Assert.Single(await world.TodosAsync(), t => t.EntityId == bareStay);
        Assert.Null(bareTodo.CompletedAt);

        // The source says the VAT now: the item the system raised is closed by the system; the one a person closed stays as it was.
        source.Set(Cleaning(property, stay, request, 7000, CleaningAmountVat.IncludesSupplierVat), Cleaning(bareProperty, bareStay, bareRequest, 9000, CleaningAmountVat.IncludesSupplierVat));
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var stated = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.DoesNotContain(stated.Issues, i => i.Blocking);
        Assert.Equal(-90m, Assert.Single(CleaningLines(await world.LiveAsync()), e => e.PropertyOwnerId == bare).Amount);
        var todos = await world.TodosAsync();
        Assert.Equal(InternalActivity.ClosedBySystem, todos.Single(t => t.EntityId == bareStay).ClosedBy);
        Assert.Equal("auth0|admin", todos.Single(t => t.EntityId == stay).ClosedBy);
    }

    /// <summary>
    /// PROOF TEST: money rule. The owner invoice shows one recharge line for each cleaning request, with its reference (and the supplier's invoice when the model links one), in
    /// place of the flat fee of the stay; a request changed after it was posted is one line, the net of its reversal and its new line.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_InvoiceShowsOneRechargeLinePerRequestWithItsReference_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var changed = Guid.NewGuid();
        var withInvoice = rig.Line(OwnerLedgerEntryType.OwnerCharge, -50m, rig.LakeHouse, OwnerLedgerSourceType.CleaningRequest, second, OwnerLedgerCleaningRecharges.ReferenceOf(second), "Pulizia del 20/10/2026");
        withInvoice.AttachmentReference = "FT-2026/17";
        var lines = new List<OwnerLedgerEntry>
        {
            rig.Line(OwnerLedgerEntryType.AgencyCommission, -200m, rig.LakeHouse, OwnerLedgerSourceType.Booking, rig.DirectStay),
            rig.Line(OwnerLedgerEntryType.OwnerCharge, -80m, rig.LakeHouse, OwnerLedgerSourceType.CleaningRequest, first, OwnerLedgerCleaningRecharges.ReferenceOf(first), "Pulizia del 20/10/2026"),
            withInvoice,
            rig.Line(OwnerLedgerEntryType.OwnerCharge, -60m, rig.LakeHouse, OwnerLedgerSourceType.CleaningRequest, changed, OwnerLedgerCleaningRecharges.ReferenceOf(changed), "Pulizia del 20/10/2026"),
            rig.Line(OwnerLedgerEntryType.OwnerCharge, 60m, rig.LakeHouse, OwnerLedgerSourceType.CleaningRequest, changed, OwnerLedgerCleaningRecharges.ReferenceOf(changed), "Pulizia del 20/10/2026"),
            rig.Line(OwnerLedgerEntryType.OwnerCharge, -75m, rig.LakeHouse, OwnerLedgerSourceType.CleaningRequest, changed, OwnerLedgerCleaningRecharges.ReferenceOf(changed), "Pulizia del 20/10/2026"),
        };
        var version = await rig.VersionAsync(lines);

        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, "auth0|agency");

        var recharges = draft.Lines.Where(l => l.LineType == FiscalLineType.Recharge).OrderBy(l => l.TotalPrice).ToList();
        Assert.Equal([50m, 75m, 80m], recharges.Select(l => l.TotalPrice));
        Assert.Equal(
            [
                "Riaddebito pulizia - TEST Casa Lago - richiesta " + OwnerLedgerCleaningRecharges.ReferenceOf(second) + " del 20/10/2026, fattura del fornitore FT-2026/17",
                "Riaddebito pulizia - TEST Casa Lago - richiesta " + OwnerLedgerCleaningRecharges.ReferenceOf(changed) + " del 20/10/2026",
                "Riaddebito pulizia - TEST Casa Lago - richiesta " + OwnerLedgerCleaningRecharges.ReferenceOf(first) + " del 20/10/2026",
            ],
            recharges.Select(l => l.Description));
        Assert.Equal(200m, draft.Lines.Single(l => l.LineType == FiscalLineType.Commission).TotalPrice);
    }

    /// <summary>
    /// PROOF TEST: data rule. The mode is a field of the mandate: the flat rate when the version does not say, <c>ActualCost</c> when it does (also through the standard mandate), an
    /// undefined value is refused with 422 <c>owner_mandate_invalid</c>, and a mandate written before the field (none stored) reads as the flat rate.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_MandateModeDefaultsToFlatRateAndRefusesAnUndefinedValue_Test()
    {
        var world = await World.CreateAsync();
        var owner = await world.OwnerAsync();
        await world.PropertyAsync(owner, "TEST Casa Lago");

        var plain = await world.Mandates.CreateVersionAsync(OrgWide, owner, new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 10m }, null);
        var actual = await world.Mandates.CreateVersionAsync(
            OrgWide,
            owner,
            new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 6, 1), CommissionPercent = 10m, CleaningRechargeMode = CleaningRechargeMode.ActualCost },
            null);

        Assert.Equal(CleaningRechargeMode.FlatRate, plain.CleaningRechargeMode);
        Assert.Equal(CleaningRechargeMode.FlatRate, OwnerMandateDto.From(plain).CleaningRechargeMode);
        Assert.Equal(CleaningRechargeMode.ActualCost, actual.CleaningRechargeMode);
        Assert.Equal(CleaningRechargeMode.ActualCost, OwnerMandateDto.From(actual).CleaningRechargeMode);
        var stored = await world.Db.OwnerMandates.AsNoTracking().OrderBy(m => m.ValidFrom).ToListAsync();
        Assert.Equal([CleaningRechargeMode.FlatRate, CleaningRechargeMode.ActualCost], stored.Select(m => OwnerMandateRules.CleaningRechargeModeOf(m)));

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Mandates.CreateVersionAsync(
            OrgWide,
            owner,
            new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 9, 1), CommissionPercent = 10m, CleaningRechargeMode = (CleaningRechargeMode)99 },
            null));
        Assert.Equal(OwnerMandateErrors.MandateInvalid, refused.Code);
        Assert.Equal("cleaningRechargeMode", refused.Extensions["field"]);
        Assert.Equal(2, await world.Db.OwnerMandates.CountAsync());

        // A mandate written before the field stores none: it reads as the flat rate.
        Assert.Equal(CleaningRechargeMode.FlatRate, OwnerMandateRules.CleaningRechargeModeOf(new OwnerMandate { CleaningRechargeMode = null }));

        // The standard mandate carries the mode to every owner it creates a version for.
        var another = await world.OwnerAsync();
        await world.PropertyAsync(another, "TEST Casa Monte");
        var template = await world.Mandates.ApplyTemplateAsync(
            OrgWide,
            new OwnerMandateTemplateCommand
            {
                Template = new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 12m, CleaningRechargeMode = CleaningRechargeMode.ActualCost },
                OwnerIds = [another],
            },
            null);
        Assert.Equal(1, template.Created);
        Assert.Equal(CleaningRechargeMode.ActualCost, (await world.Mandates.ListAsync(OrgWide, another)).Single().CleaningRechargeMode);
    }

    /// <summary>
    /// PROOF TEST: money rule. A property of two owners (60% and 40%) whose mandates differ, the first on the actual cost and the second on the flat rate, is never charged both ways:
    /// the cleaning request goes only to the owner on the actual cost, for its share of the price (the cents the rounding leaves are the last share's), the flat fee of the stay goes
    /// only to the other, for its share of the fee, and nobody gets both. Posting again changes nothing; a request whose price changes reverses and replaces only the line of the owner on
    /// the actual cost.
    /// </summary>
    [Fact]
    public async Task CleaningRecharge_CoOwnersOnDifferentModes_EachPaysItsShareOnce_Test()
    {
        var world = await World.CreateAsync();
        var actual = await world.OwnerAsync();
        var flat = await world.OwnerAsync();
        var property = await world.CoOwnedPropertyAsync("TEST Casa Lago", (actual, 60m), (flat, 40m));
        await world.MandateAsync(actual, CleaningRechargeMode.ActualCost, 40m);
        await world.MandateAsync(flat, CleaningRechargeMode.FlatRate, 40m);
        var stay = await world.StayAsync(property, Day(10, 17), Day(10, 20));
        var request = await world.RequestAsync(property, ServiceRequestStatus.Completato, At(10, 20), r =>
        {
            r.BookingId = stay;
            r.FinalAmountCents = 8501;
        });

        var result = await world.Service.PostPeriodAsync(OrgId, 2026, 10, "auth0|admin");

        Assert.DoesNotContain(result.Issues, i => i.Blocking);
        var lines = await world.LiveAsync();

        // The request: only the owner on the actual cost has a line of it, its 60% of 85.01 (51.006, rounded to 51.01); the owner on the flat rate has none.
        var cleaned = Assert.Single(CleaningLines(lines));
        Assert.Equal((actual, request, 60m), (cleaned.PropertyOwnerId, cleaned.SourceId!.Value, cleaned.SharePercent));
        Assert.Equal((-85.01m, -51.01m, -51.01m), (cleaned.SourceAmount, cleaned.OriginalAmount, cleaned.Amount));

        // The flat fee of the stay: only the owner on the flat rate has it, its 40% of 40.00; the owner on the actual cost has none (its mandate has the same flat rate, never posted).
        var fee = Assert.Single(lines, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee);
        Assert.Equal((flat, stay, OwnerLedgerFlatFees.Cleaning, 40m), (fee.PropertyOwnerId, fee.SourceId!.Value, fee.SourceReference, fee.SharePercent));
        Assert.Equal(-16.00m, fee.Amount);

        // Each owner is charged its share of the cleaning once, by one way only.
        decimal ChargedTo(Guid owner) => lines.Where(e => e.PropertyOwnerId == owner && e.EntryType == OwnerLedgerEntryType.OwnerCharge).Sum(e => e.Amount);
        Assert.Equal((-51.01m, -16.00m), (ChargedTo(actual), ChargedTo(flat)));

        // Posting again changes nothing.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var again = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((0, 0), (again.Posted, again.Reversed));
        Assert.Equal(lines.Select(e => e.Id).Order(), (await world.LiveAsync()).Select(e => e.Id).Order());

        // The price of the request changes: only the line of the owner on the actual cost is reversed and replaced (60% of 90.00); the flat fee of the other stays the same line.
        await world.ChangeRequestAsync(request, r => r.FinalAmountCents = 9000);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var changed = await world.Service.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal((1, 1), (changed.Posted, changed.Reversed));
        var after = await world.LiveAsync();
        var replaced = Assert.Single(CleaningLines(after));
        Assert.Equal((actual, -54.00m), (replaced.PropertyOwnerId, replaced.Amount));
        Assert.Equal(fee.Id, Assert.Single(after, e => e.SourceType == OwnerLedgerSourceType.MandateFlatFee).Id);
    }

    private static CompletedCleaning Cleaning(Guid property, Guid? stay, Guid request, long cents, CleaningAmountVat vat) =>
        new(property, stay, new DateOnly(2026, 10, 20), cents, "CHF", vat, CleaningKind.Supplier, true, CleaningSourceKind.ServiceRequest, request, null);

    /// <summary>A cost source that returns what the test says, as the contract defines it (the days included, the properties asked for, an empty list is nothing).</summary>
    private sealed class StubCostSource : ICleaningCostSource
    {
        private readonly List<CompletedCleaning> cleanings = [];

        public void Set(params CompletedCleaning[] values)
        {
            cleanings.Clear();
            cleanings.AddRange(values);
        }

        public Task<IReadOnlyList<CompletedCleaning>> GetCompletedAsync(Guid orgId, IReadOnlyCollection<Guid>? propertyIds, DateOnly from, DateOnly to, CancellationToken ct)
        {
            var found = cleanings
                .Where(c => c.CompletedOn >= from && c.CompletedOn <= to && (propertyIds is null || propertyIds.Contains(c.PropertyId)))
                .ToList();
            return Task.FromResult<IReadOnlyList<CompletedCleaning>>(found);
        }
    }

    /// <summary>
    /// A Swiss agency with its owners, properties, stays, cleaning requests and staff cleanings, the owner accounting (with the real cost source of the cleanings, or the one the test
    /// gives) and the mandates over one in-memory database.
    /// </summary>
    private sealed class World
    {
        private World(AppDbContext db, ICleaningCostSource? costs)
        {
            Db = db;
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
            var options = Microsoft.Extensions.Options.Options.Create(new OwnerAccountingOptions());
            Fiscal = new OwnerFiscalProfileService(db, Clock, NullLogger<OwnerFiscalProfileService>.Instance);
            Activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock);
            Service = new OwnerAccountingService(
                db,
                Fiscal,
                Clock,
                NullLogger<OwnerAccountingService>.Instance,
                options,
                cleaningCosts: costs ?? new CleaningCostSource(db),
                activities: Activities);
            Mandates = new OwnerMandateService(db, Clock, options, NullLogger<OwnerMandateService>.Instance);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public InternalActivityService Activities { get; }

        public OwnerAccountingService Service { get; }

        public OwnerMandateService Mandates { get; }

        public static Task<World> CreateAsync(ICleaningCostSource? costs = null) => Task.FromResult(new World(
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"CleaningRecharge_{Guid.NewGuid()}").Options,
                new CleaningRechargeTenant(),
                new EphemeralDataProtectionProvider()),
            costs));

        /// <summary>A Swiss owner paid in francs, with a complete profile from January and the approved Swiss rule set (created once).</summary>
        public async Task<Guid> OwnerAsync()
        {
            var owner = new PropertyOwner { OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = $"{Guid.NewGuid():N}@example.com" };
            Db.PropertyOwners.Add(owner);
            await Db.SaveChangesAsync();

            var policy = (await Fiscal.ListPoliciesAsync(OrgId)).FirstOrDefault();
            if (policy is null)
            {
                policy = await Fiscal.CreatePolicyAsync(OrgId, new OwnerAccountingPolicyCommand
                {
                    Jurisdiction = "CH",
                    RuleCode = "TEST-CH",
                    Version = "1",
                    ValidFrom = new DateOnly(2026, 1, 1),
                    SourceUrl = "https://example.com/test-rule",
                    SourceSha256 = new string('d', 64),
                    Parameters = new OwnerAccountingRuleParameters { WithholdingApplies = false, Rounding = "half_even", RetentionYears = 10 },
                }, null);
                await Fiscal.ApprovePolicyAsync(OrgId, policy.Id, "TEST Commercialista", null);
            }

            await Fiscal.SaveProfileAsync(OrgId, owner.Id, new OwnerFiscalProfileCommand
            {
                ValidFrom = new DateOnly(2026, 1, 1),
                EntityKind = OwnerEntityKind.NaturalPerson,
                LegalName = "TEST Proprietario",
                TaxResidenceCountry = "CH",
                LegalAddress = "Via del Test 1, Lugano",
                ForeignTaxId = "756123456789",
                SettlementCurrency = "CHF",
                BankAccountReference = "CH9300762011623852957",
                RevenueRecognitionRule = "cash",
                TaxJurisdiction = "CH",
                AccountingPolicyId = policy.Id,
            }, null);
            return owner.Id;
        }

        /// <summary>A Ticino property in francs owned from January by the owner alone.</summary>
        public async Task<Guid> PropertyAsync(Guid owner, string name)
        {
            var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = name, Address = "Via 1", City = "Lugano", Currency = "CHF" };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = property.Id,
                PropertyOwnerId = owner,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2026, 1, 1),
            });
            await Db.SaveChangesAsync();
            return property.Id;
        }

        /// <summary>A Ticino property in francs owned from January by two owners, each in the share given.</summary>
        public async Task<Guid> CoOwnedPropertyAsync(string name, (Guid Owner, decimal Share) first, (Guid Owner, decimal Share) second)
        {
            var property = new Property { OrgId = OrgId, OwnerId = "auth0|agency", Name = name, Address = "Via 1", City = "Lugano", Currency = "CHF" };
            Db.Properties.Add(property);
            foreach (var (owner, share) in new[] { first, second })
            {
                Db.PropertyOwnerships.Add(new PropertyOwnership
                {
                    OrgId = OrgId,
                    PropertyId = property.Id,
                    PropertyOwnerId = owner,
                    SharePercent = share,
                    OwnershipStart = new DateTime(2026, 1, 1),
                });
            }

            await Db.SaveChangesAsync();
            return property.Id;
        }

        /// <summary>A mandate version of the owner from the day, through the API's service: no commission, the cleaning flat rate and the mode as given.</summary>
        public Task<OwnerMandate> MandateAsync(Guid owner, CleaningRechargeMode mode, decimal? cleaningFlatRate, DateOnly? from = null) =>
            Mandates.CreateVersionAsync(
                OrgWide,
                owner,
                new OwnerMandateCommand
                {
                    ValidFrom = from ?? new DateOnly(2026, 1, 1),
                    CommissionPercent = 0m,
                    CleaningFlatRate = cleaningFlatRate,
                    CleaningRechargeMode = mode,
                },
                null);

        /// <summary>A confirmed stay of the property, priced by hand: its gross is an amount, so it posts its revenue on the check-out and tells nothing.</summary>
        public async Task<Guid> StayAsync(Guid property, DateTime checkIn, DateTime checkOut)
        {
            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property,
                GuestId = guest.Id,
                CheckInDate = checkIn,
                CheckOutDate = checkOut,
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Manual,
                BasePrice = 300m,
                TotalPrice = 300m,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            await Db.SaveChangesAsync();
            return booking.Id;
        }

        /// <summary>A request of category cleaning for the property, written straight to the table as the cost source reads it.</summary>
        public async Task<Guid> RequestAsync(Guid property, ServiceRequestStatus status, DateTime? completedAt, Action<ServiceRequest>? change = null)
        {
            var request = new ServiceRequest
            {
                OrgId = OrgId,
                PropertyId = property,
                SupplierOrgId = Guid.NewGuid(),
                RentalContext = ServiceRequestRentalContext.ShortRent,
                Category = ServiceCategories.Cleaning,
                Status = status,
                CompletedAt = completedAt,
                CreatedAt = At(9, 1),
                UpdatedAt = At(9, 1),
            };
            change?.Invoke(request);
            Db.ServiceRequests.Add(request);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return request.Id;
        }

        public async Task ChangeRequestAsync(Guid requestId, Action<ServiceRequest> change)
        {
            var request = await Db.ServiceRequests.SingleAsync(r => r.Id == requestId);
            change(request);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>A cleaning by the agency's staff, completed, with or without a rate.</summary>
        public async Task<Guid> StaffAsync(Guid property, DateTime completedAt, decimal? rate, bool recharge)
        {
            var activity = new InternalActivity
            {
                OrgId = OrgId,
                PropertyId = property,
                StaffUserId = "auth0|staff",
                ActivityType = InternalActivity.CleaningActivityType,
                Status = InternalActivity.StatusCompleted,
                ScheduledAt = completedAt,
                CompletedAt = completedAt,
                InternalRate = rate,
                RechargeToOwner = recharge,
            };
            Db.InternalActivities.Add(activity);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return activity.Id;
        }

        /// <summary>The «Da fare» rows (the closed ones too), oldest first.</summary>
        public async Task<List<InternalActivity>> TodosAsync() =>
            await Db.InternalActivities.AsNoTracking().Where(a => a.Action != null).OrderBy(a => a.CreatedAt).ToListAsync();

        /// <summary>The lines that count: neither reversals nor reversed.</summary>
        public async Task<List<OwnerLedgerEntry>> LiveAsync()
        {
            var lines = await Db.OwnerLedgerEntries.AsNoTracking().ToListAsync();
            var reversed = lines.Where(e => e.ReversesEntryId is not null).Select(e => e.ReversesEntryId!.Value).ToHashSet();
            return lines.Where(e => e.ReversesEntryId is null && !reversed.Contains(e.Id)).ToList();
        }
    }
}

file sealed class CleaningRechargeTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
