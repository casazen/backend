using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.ServiceRequests;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-CLEANING-VAT (decision 4 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>, research R4): the agency answers «La ditta di pulizie ha applicato l'IVA su questa
/// fattura?» for a cleaning recharged to the owner; the rates of «Sì» come from a dated table; the answer is stored on the request, remembered for the company when asked, closes
/// the «Da fare» and comes back once at the close of the month when it was «Non lo so»; the line of the recharge on the invoice of the agency carries the company's VAT, contained
/// in the gross, while the ledger and the statements stay gross; another organization's request is never reached.
/// </summary>
public class CleaningVatTests
{
    private const string Agency = "auth0|agency";

    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OtherOrgId = Guid.NewGuid();
    private static readonly HostScope OrgWide = new(OrgId);

    /// <summary>The instant a cleaning was completed: 9:00 UTC, the same Europe/Rome day.</summary>
    private static DateTime At(int month, int day) => new(2026, month, day, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// PROOF TEST: data rule. The rates are a table with dated validity, not constants: Switzerland 8.1, 2.6 and 3.8 (until 31/12/2027 by law), Italy 22 and 10. A day outside
    /// every validity of a kind (the Swiss lodging rate in 2028, any rate before 2024), a kind a country has no row of, a country the table does not know: no rate, never another
    /// row or a default. The question reads the table: a cleaning of 2023 has no rate for «Sì», which is refused and stores nothing, while «No» needs no rate.
    /// </summary>
    [Fact]
    public async Task VatRates_AreDatedAndNeverDefaultOutsideTheirValidity_Test()
    {
        var day = new DateOnly(2026, 10, 20);
        Assert.Equal(8.1m, VatRates.Find("CH", VatRateKind.Standard, day)!.Percent);
        Assert.Equal(2.6m, VatRates.Find("CH", VatRateKind.Reduced, day)!.Percent);
        Assert.Equal(3.8m, VatRates.Find("CH", VatRateKind.Lodging, new DateOnly(2027, 12, 31))!.Percent);
        Assert.Equal(22m, VatRates.Find("it", VatRateKind.Standard, day)!.Percent);
        Assert.Equal(10m, VatRates.Find("IT", VatRateKind.Lodging, day)!.Percent);

        // Outside the validity of every row of the kind there is no rate.
        Assert.Null(VatRates.Find("CH", VatRateKind.Lodging, new DateOnly(2028, 1, 1)));
        Assert.Null(VatRates.Find("CH", VatRateKind.Standard, new DateOnly(2023, 12, 31)));
        Assert.Null(VatRates.Find("IT", VatRateKind.Standard, new DateOnly(2023, 12, 31)));
        Assert.Null(VatRates.Find("IT", VatRateKind.Reduced, day));
        Assert.Null(VatRates.Find("FR", VatRateKind.Standard, day));
        Assert.Null(VatRates.Find(null, VatRateKind.Standard, day));

        // Every row is dated and has its source; two rows of one country and kind never overlap, so a day has one rate or none.
        Assert.All(VatRates.Table, rate => Assert.True(rate.ValidTo is not { } to || to >= rate.ValidFrom));
        Assert.All(VatRates.Table, rate => Assert.False(string.IsNullOrWhiteSpace(rate.Source)));
        foreach (var group in VatRates.Table.GroupBy(rate => (rate.Country, rate.Kind)))
        {
            var rows = group.OrderBy(rate => rate.ValidFrom).ToList();
            for (var i = 1; i < rows.Count; i++)
                Assert.True(rows[i - 1].ValidTo is { } end && end < rows[i].ValidFrom, $"{group.Key}: two rows overlap");
        }

        // The question reads the table on the day of the cleaning.
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var property = await world.PropertyAsync(owner, "TEST Casa Lago");
        var recent = await world.RequestAsync(property, Guid.NewGuid(), At(10, 20), 10000);
        var old = await world.RequestAsync(property, Guid.NewGuid(), new DateTime(2023, 12, 20, 9, 0, 0, DateTimeKind.Utc), 10000);

        var question = await world.Vat.GetAsync(OrgWide, recent);
        Assert.Equal(("CH", day, (decimal?)8.1m), (question.Country, question.CleaningDate, question.StandardRatePercent));
        Assert.Null((await world.Vat.GetAsync(OrgWide, old)).StandardRatePercent);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => world.Vat.AnswerAsync(OrgWide, old, CleaningSupplierVat.Applied, false, Agency));
        Assert.Equal(CleaningVatErrors.RateUnknown, refused.Code);
        Assert.Null((await world.RequestRowAsync(old)).SupplierVat);
        Assert.Equal(CleaningSupplierVat.NotApplied, (await world.Vat.AnswerAsync(OrgWide, old, CleaningSupplierVat.NotApplied, false, Agency)).Answer);
    }

    /// <summary>
    /// PROOF TEST: money rule. On the invoice of the agency (a Swiss agency, a mandate «+ IVA») the recharge of a cleaning of 100.00 gross carries the VAT of the company: «Sì»
    /// 8.1% contained in the 100.00 (VAT 7.49, net 92.51), «No» none (100.00), «Non lo so» and no answer exactly as before (8.1% added on top: 8.10). The issue posts on top only the
    /// VAT of the lines that did not contain theirs (97.20 = 81.00 + 8.10 + 8.10, not 104.69), so the owner pays each cleaning at its gross; the ledger lines of the cleanings stay
    /// 100.00, gross and not decomposed; a partial credit note gives back only its share of the VAT posted on top. In Italy «Sì» 22% contains 18.03 in 100.00 (net 81.97) and «No»
    /// is a line without VAT of nature N2.2.
    /// </summary>
    [Fact]
    public async Task CleaningVat_StatedRate_IsCarriedByTheRechargeRow_GrossUnchanged_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        await InCountryAsync(rig, "CH");
        var applied = await AnsweredRequestAsync(rig, CleaningSupplierVat.Applied, 8.1m);
        var notApplied = await AnsweredRequestAsync(rig, CleaningSupplierVat.NotApplied, null);
        var unknown = await AnsweredRequestAsync(rig, CleaningSupplierVat.Unknown, null);
        var unanswered = await AnsweredRequestAsync(rig, null, null);
        var version = await rig.VersionAsync(
        [
            rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse),
            CleaningLine(rig, applied),
            CleaningLine(rig, notApplied),
            CleaningLine(rig, unknown),
            CleaningLine(rig, unanswered),
        ]);

        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, Agency);

        // «Sì»: the row carries 8.1 and the 100.00 of the ledger contains it: VAT = 100.00 × 8.1 / 108.1 = 7.49, net 92.51.
        var yes = RowOf(draft, applied);
        Assert.Equal((8.1m, (decimal?)7.49m, 92.51m), (yes.VatRatePercent, yes.VatAmount, yes.TotalPrice));

        // «No»: no VAT, the row is the 100.00 (a Swiss invoice has no nature code).
        var no = RowOf(draft, notApplied);
        Assert.Equal((0m, 0m, 100.00m, (string?)null), (no.VatRatePercent, no.VatAmount ?? 0m, no.TotalPrice, no.VatNatureCode));

        // «Non lo so», and nobody answered: exactly as before the question, the rate of a recharge added on top of the 100.00.
        Assert.All(
            new[] { RowOf(draft, unknown), RowOf(draft, unanswered) },
            row => Assert.Equal((8.1m, (decimal?)8.10m, 100.00m), (row.VatRatePercent, row.VatAmount, row.TotalPrice)));
        Assert.Equal((1000.00m, (decimal?)81.00m), (draft.Lines.Single(l => l.LineType == FiscalLineType.Commission).TotalPrice, draft.Lines.Single(l => l.LineType == FiscalLineType.Commission).VatAmount));
        Assert.Equal((1392.51m, 104.69m, 1497.20m), (draft.TaxableBase, draft.VatAmount, draft.Total));

        // The issue posts on top only the VAT of the lines that did not contain theirs: the ledger then owes exactly the total of the invoice.
        var before = await rig.Db.OwnerLedgerEntries.AsNoTracking().SumAsync(e => e.Amount);
        var issued = await rig.Invoices.IssueAsync(rig.OrgId, draft.Id, confirm: true, Agency);
        var vat = Assert.Single(await rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.EntryType == OwnerLedgerEntryType.AgencyVat).ToListAsync());
        Assert.Equal(-97.20m, vat.Amount);
        Assert.Equal((-1400.00m, -1497.20m), (before, before + vat.Amount));
        Assert.Equal(issued.Total, -(before + vat.Amount));

        // The ledger stays gross and not decomposed: each cleaning is still its 100.00.
        var cleanings = await rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.SourceType == OwnerLedgerSourceType.CleaningRequest).ToListAsync();
        Assert.Equal(4, cleanings.Count);
        Assert.All(cleanings, e => Assert.Equal(-100.00m, e.Amount));

        // A partial credit note gives back its share of the VAT posted on top (97.20 of the 104.69 of the invoice), never the VAT the line of the «Sì» cleaning holds.
        var note = await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, issued.Id, 100.00m, "TEST storno parziale", confirm: true, Agency);
        var givenBack = Assert.Single(await rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.EntryType == OwnerLedgerEntryType.AgencyVat && e.SourceId == note.Id).ToListAsync());
        Assert.Equal(Math.Round(note.VatAmount * 97.20m / 104.69m, 2, MidpointRounding.AwayFromZero), givenBack.Amount);
        Assert.True(givenBack.Amount < note.VatAmount, $"{givenBack.Amount} of a note of VAT {note.VatAmount}");

        // Italy (an issuer of the ordinary regime): «Sì» 22% → 100.00 contains 18.03 (net 81.97); «No» → no VAT, nature N2.2. The commission keeps its 22% on top.
        await using var italian = await OwnerInvoiceRig.SwissAsync();
        await ItalianIssuerAsync(italian);
        var italianYes = await AnsweredRequestAsync(italian, CleaningSupplierVat.Applied, 22m);
        var italianNo = await AnsweredRequestAsync(italian, CleaningSupplierVat.NotApplied, null);
        var italianVersion = await italian.VersionAsync(
        [
            italian.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, italian.LakeHouse),
            CleaningLine(italian, italianYes),
            CleaningLine(italian, italianNo),
        ]);

        var italianDraft = await italian.Invoices.CreateDraftAsync(italian.OrgId, italianVersion.Id, Agency);

        var si = RowOf(italianDraft, italianYes);
        Assert.Equal((22m, (decimal?)18.03m, 81.97m), (si.VatRatePercent, si.VatAmount, si.TotalPrice));
        var senza = RowOf(italianDraft, italianNo);
        Assert.Equal((0m, "N2.2", 100.00m), (senza.VatRatePercent, senza.VatNatureCode, senza.TotalPrice));
        Assert.Equal(220.00m + 18.03m, italianDraft.VatAmount);
    }

    /// <summary>
    /// PROOF TEST: data rule. The posting asks the question about each cleaning of a company nobody stated, once (an item of the stay raised before keeps the request from a second
    /// one); the answer is stored on the request with the rate it used, who and when; «Ricorda per questa ditta» keeps it for the company in the organization, which states and
    /// closes the item of the other cleanings of the company; the same answer again changes nothing, another one replaces it once; «Non lo so» is never remembered, closes the item
    /// (and the item of its stay) and is not asked again at the posting; at the close of the month it is proposed once more, once for the request even when a second owner closes,
    /// and it never blocks the close.
    /// </summary>
    [Fact]
    public async Task CleaningVat_AnswerIsStoredRememberedPerSupplier_AndClosesTheTodo_Test()
    {
        var world = World.Create();
        var first = await world.OwnerAsync();
        var second = await world.OwnerAsync();
        var property = await world.CoOwnedPropertyAsync("TEST Casa Lago", (first, 60m), (second, 40m));
        await world.MandateAsync(first);
        await world.MandateAsync(second);
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var firstStay = await world.StayAsync(property, At(10, 17), At(10, 20));
        var secondStay = await world.StayAsync(property, At(10, 20), At(10, 22));
        var a1 = await world.RequestAsync(property, companyA, At(10, 20), 10000, firstStay);
        var a2 = await world.RequestAsync(property, companyA, At(10, 21), 8500);
        var b = await world.RequestAsync(property, companyB, At(10, 22), 9000, secondStay);

        // An item raised before FIX/BE-CLEANING-VAT about the second stay (ReviewBooking): it keeps working and keeps the request of the stay from a second item.
        var legacy = await world.Activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.CleaningVatToState,
                Action = ActivityAction.ReviewBooking,
                EntityType = InternalActivity.BookingEntityType,
                EntityId = secondStay,
                Urgency = ActivityUrgency.Medium,
                PropertyId = property,
                Reason = "TEST",
            },
            CancellationToken.None);

        await world.Accounting.PostPeriodAsync(OrgId, 2026, 10, Agency);

        var raised = (await world.TodosAsync()).Where(t => t.Id != legacy).ToList();
        Assert.Equal(new[] { a1, a2 }.Order(), raised.Select(t => t.EntityId!.Value).Order());
        Assert.All(raised, t => Assert.Equal(
            (nameof(ActivityType.CleaningVatToState), (ActivityAction?)ActivityAction.StateCleaningVat, (string?)CleaningVatRules.TodoEntityType, (Guid?)property, (DateTime?)null),
            (t.ActivityType, t.Action, t.EntityType, t.PropertyId, t.CompletedAt)));
        Assert.All(raised, t => Assert.False(string.IsNullOrWhiteSpace(t.Reason)));
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Accounting.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal(3, (await world.TodosAsync()).Count);

        // «Sì» for a1, remembered for the company: stored on the request with the rate it used, who and when; kept for company A; the items of a1 and a2 are closed.
        var answeredAt = world.Clock.GetUtcNow().UtcDateTime;
        var yes = await world.Vat.AnswerAsync(OrgWide, a1, CleaningSupplierVat.Applied, remember: true, Agency);
        Assert.Equal(
            (CleaningSupplierVat.Applied, 8.1m, (decimal?)8.1m, true, CleaningSupplierVat.Applied, false),
            (yes.Answer!.Value, yes.AnswerRatePercent!.Value, yes.StandardRatePercent, yes.CompanyAppliesVat!.Value, yes.Stated!.Answer, yes.Stated.FromCompany));
        var row = await world.RequestRowAsync(a1);
        Assert.Equal(
            (CleaningSupplierVat.Applied, 8.1m, answeredAt, (string?)Agency),
            (row.SupplierVat!.Value, row.SupplierVatRatePercent!.Value, row.SupplierVatAnsweredAt!.Value, row.SupplierVatAnsweredByUserId));
        var remembered = Assert.Single(await world.Db.SupplierVatDefaults.AsNoTracking().ToListAsync());
        Assert.Equal((OrgId, companyA, true, (string?)Agency), (remembered.OrgId, remembered.SupplierOrgId, remembered.AppliesVat, remembered.UpdatedByUserId));
        var todos = await world.TodosAsync();
        Assert.Equal(Agency, todos.Single(t => t.EntityId == a1).ClosedBy);
        Assert.Equal(Agency, todos.Single(t => t.EntityId == a2).ClosedBy);
        Assert.Null(todos.Single(t => t.Id == legacy).CompletedAt);

        // a2 has no answer of its own: the company's states it, with the rate of the table on its own day.
        var byCompany = await world.Vat.GetAsync(OrgWide, a2);
        Assert.Equal(((CleaningSupplierVat?)null, (decimal?)8.1m, true), (byCompany.Answer, byCompany.Stated!.RatePercent, byCompany.Stated.FromCompany));

        // The same answer again changes nothing; another one replaces it, once.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Vat.AnswerAsync(OrgWide, a1, CleaningSupplierVat.Applied, remember: true, Agency);
        Assert.Equal(answeredAt, (await world.RequestRowAsync(a1)).SupplierVatAnsweredAt);
        Assert.Single(await world.Db.SupplierVatDefaults.AsNoTracking().ToListAsync());
        var replacedAt = world.Clock.GetUtcNow().UtcDateTime;
        await world.Vat.AnswerAsync(OrgWide, a1, CleaningSupplierVat.NotApplied, remember: false, "auth0|other");
        row = await world.RequestRowAsync(a1);
        Assert.Equal(
            (CleaningSupplierVat.NotApplied, (decimal?)null, replacedAt, (string?)"auth0|other"),
            (row.SupplierVat!.Value, row.SupplierVatRatePercent, row.SupplierVatAnsweredAt!.Value, row.SupplierVatAnsweredByUserId));
        Assert.True(Assert.Single(await world.Db.SupplierVatDefaults.AsNoTracking().ToListAsync()).AppliesVat);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Vat.AnswerAsync(OrgWide, a1, CleaningSupplierVat.NotApplied, remember: false, Agency);
        Assert.Equal(replacedAt, (await world.RequestRowAsync(a1)).SupplierVatAnsweredAt);

        // «Non lo so» is never remembered: refused, nothing written.
        var unknownRemembered = await Assert.ThrowsAsync<DomainRuleException>(() => world.Vat.AnswerAsync(OrgWide, b, CleaningSupplierVat.Unknown, remember: true, Agency));
        Assert.Equal(CleaningVatErrors.RememberUnknown, unknownRemembered.Code);
        Assert.Null((await world.RequestRowAsync(b)).SupplierVat);
        var missing = await Assert.ThrowsAsync<DomainRuleException>(() => world.Vat.AnswerAsync(OrgWide, b, null, remember: false, Agency));
        Assert.Equal(CleaningVatErrors.AnswerInvalid, missing.Code);

        // «Non lo so» for b closes the item of its stay (every cleaning of the stay has an answer now); the posting asks nothing again.
        await world.Vat.AnswerAsync(OrgWide, b, CleaningSupplierVat.Unknown, remember: false, Agency);
        Assert.Equal(Agency, (await world.TodosAsync()).Single(t => t.Id == legacy).ClosedBy);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Accounting.PostPeriodAsync(OrgId, 2026, 10, null);
        Assert.Equal(3, (await world.TodosAsync()).Count);

        // The close of the first owner's month: «Non lo so» never blocks it, and the question of b comes back once.
        await world.ApprovedVersionAsync(first);
        await world.ApprovedVersionAsync(second);
        var closed = await world.Accounting.CloseAsync(OrgId, first, 2026, 10, new OwnerCloseCommand { WaiverReason = "TEST" }, mayWaive: true, Agency);
        Assert.Equal(OwnerClosePeriodStatus.Closed, closed.Status);
        var again = Assert.Single(await world.TodosAsync(), t => t.EntityId == b);
        Assert.Equal((ActivityAction.StateCleaningVat, (DateTime?)null), (again.Action!.Value, again.CompletedAt));
        Assert.NotEqual(Assert.Single(await world.TodosAsync(), t => t.EntityId == a1).Reason, again.Reason);
        Assert.NotNull((await world.RequestRowAsync(b)).SupplierVatAskedAgainAt);
        Assert.Null((await world.RequestRowAsync(a1)).SupplierVatAskedAgainAt);
        Assert.Null((await world.RequestRowAsync(a2)).SupplierVatAskedAgainAt);

        // «Non lo so» again closes it; the close of the second owner, who shares the cleaning, does not propose it a third time.
        await world.Vat.AnswerAsync(OrgWide, b, CleaningSupplierVat.Unknown, remember: false, Agency);
        Assert.Equal(Agency, Assert.Single(await world.TodosAsync(), t => t.EntityId == b).ClosedBy);
        await world.Accounting.CloseAsync(OrgId, second, 2026, 10, new OwnerCloseCommand { WaiverReason = "TEST" }, mayWaive: true, Agency);
        Assert.Single(await world.TodosAsync(), t => t.EntityId == b);
        Assert.Equal(4, (await world.TodosAsync()).Count);
    }

    /// <summary>
    /// PROOF TEST: permission and tenant rule. The request of another organization, of a property a collaborator «Solo alcuni» does not reach, or of another category is the 404
    /// of one that does not exist, and is never written; the company's remembered answer is the organization's own, never another's; the endpoint needs the permission on the
    /// property of the request (403 and nothing stored without it) and «Ricorda per questa ditta» needs the whole organization (403 <c>cleaning_vat_remember_forbidden</c>).
    /// </summary>
    [Fact]
    public async Task CleaningVat_IsScopedToTheOrganizationAndPermission_Test()
    {
        var world = World.Create();
        var owner = await world.OwnerAsync();
        var reached = await world.PropertyAsync(owner, "TEST Casa Lago");
        var notReached = await world.PropertyAsync(owner, "TEST Casa Monte");
        var company = Guid.NewGuid();
        var mine = await world.RequestAsync(reached, company, At(10, 20), 10000);
        var outOfReach = await world.RequestAsync(notReached, company, At(10, 21), 10000);
        var maintenance = await world.RequestAsync(reached, company, At(10, 22), 10000, change: r => r.Category = "maintenance");
        var otherProperty = await world.PropertyAsync(owner, "TEST Casa altrui", OtherOrgId);
        var theirs = await world.RequestAsync(otherProperty, company, At(10, 20), 10000, orgId: OtherOrgId);

        // Another organization's request, and a request of another category: not found, nothing written.
        foreach (var id in new[] { theirs, maintenance })
        {
            var read = await Assert.ThrowsAsync<NotFoundException>(() => world.Vat.GetAsync(OrgWide, id));
            Assert.Equal(ServiceRequestErrorCodes.NotFound, read.Code);
            var write = await Assert.ThrowsAsync<NotFoundException>(() => world.Vat.AnswerAsync(OrgWide, id, CleaningSupplierVat.Applied, remember: true, Agency));
            Assert.Equal(ServiceRequestErrorCodes.NotFound, write.Code);
        }

        Assert.Null((await world.RequestRowAsync(theirs)).SupplierVat);
        Assert.Empty(await world.Db.SupplierVatDefaults.AsNoTracking().ToListAsync());

        // A collaborator «Solo alcuni» reaches the requests of the properties it was given, and only those.
        world.Db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = OrgId, UserId = "auth0|member", PropertyId = reached });
        await world.Db.SaveChangesAsync();
        var member = new HostScope(OrgId, GrantedToUserId: "auth0|member");
        Assert.Equal(mine, (await world.Vat.GetAsync(member, mine)).ServiceRequestId);
        var outOfScope = await Assert.ThrowsAsync<NotFoundException>(() => world.Vat.AnswerAsync(member, outOfReach, CleaningSupplierVat.Applied, remember: false, "auth0|member"));
        Assert.Equal(ServiceRequestErrorCodes.NotFound, outOfScope.Code);
        Assert.Null((await world.RequestRowAsync(outOfReach)).SupplierVat);

        // The other organization remembers «No» for the same company: it states nothing of this organization's requests.
        await world.Vat.AnswerAsync(new HostScope(OtherOrgId), theirs, CleaningSupplierVat.NotApplied, remember: true, "auth0|other-agency");
        var ours = await world.Vat.GetAsync(OrgWide, mine);
        Assert.Equal(((bool?)null, (StatedCleaningVat?)null), (ours.CompanyAppliesVat, ours.Stated));

        // The endpoint: the permission on the property of the request decides (403, nothing stored), and only the whole organization remembers for the company.
        var refused = Controller(world, member, mayWrite: false);
        Assert.IsType<ForbidResult>((await refused.Answer(mine, new CleaningVatAnswerRequest { Answer = CleaningSupplierVat.Applied }, CancellationToken.None)).Result);
        Assert.Null((await world.RequestRowAsync(mine)).SupplierVat);

        var restricted = Controller(world, member, mayWrite: true);
        var forbidden = Assert.IsType<ObjectResult>((await restricted.Answer(mine, new CleaningVatAnswerRequest { Answer = CleaningSupplierVat.Applied, Remember = true }, CancellationToken.None)).Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(CleaningVatErrors.RememberForbidden, Assert.IsAssignableFrom<ProblemDetails>(forbidden.Value).Extensions["code"]);
        Assert.Null((await world.RequestRowAsync(mine)).SupplierVat);
        Assert.Single(await world.Db.SupplierVatDefaults.AsNoTracking().ToListAsync());

        var answered = Assert.IsType<OkObjectResult>((await restricted.Answer(mine, new CleaningVatAnswerRequest { Answer = CleaningSupplierVat.Applied }, CancellationToken.None)).Result);
        var dto = Assert.IsType<CleaningVatDto>(answered.Value);
        Assert.Equal((CleaningSupplierVat.Applied, (decimal?)8.1m, false), (dto.Answer!.Value, dto.AnswerRatePercent, dto.CanRemember));
        Assert.True(Assert.IsType<CleaningVatDto>(Assert.IsType<OkObjectResult>((await Controller(world, OrgWide, mayWrite: true).Get(mine, CancellationToken.None)).Result).Value).CanRemember);

        // The doors of the endpoint: property.read to read, property.write to answer (in either rental context: the request's own decides on the resource).
        Assert.Equal(CasazenPolicies.SharedPropertyRead, PolicyOf(nameof(ServiceRequestCleaningVatController.Get)));
        Assert.Equal(CasazenPolicies.SharedPropertyWrite, PolicyOf(nameof(ServiceRequestCleaningVatController.Answer)));
    }

    // ---------------------------------------------------------------- the owner invoice rig

    /// <summary>The properties of the rig in <paramref name="country"/>: the country of the VAT of their cleanings.</summary>
    private static async Task InCountryAsync(OwnerInvoiceRig rig, string country)
    {
        foreach (var property in await rig.Db.Properties.ToListAsync())
            property.Country = country;
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
    }

    /// <summary>The agency of the rig as an Italian issuer of the ordinary regime, its owner and properties in Italy (the data of an Italian invoice).</summary>
    private static async Task ItalianIssuerAsync(OwnerInvoiceRig rig)
    {
        var org = await rig.Db.Orgs.SingleAsync(o => o.Id == rig.OrgId);
        org.HasPartitaIva = true;
        org.PartitaIvaNumber = "01234567897";
        org.FiscalCode = "01234567897";
        var profile = await rig.Db.OrgFiscalIssuerProfiles.SingleAsync(p => p.OrgId == rig.OrgId);
        profile.DocumentRegime = DocumentRegime.VatOrdinary;
        profile.CountryCode = "IT";
        profile.LegalName = "TEST Agenzia Srl";
        profile.PostalCode = "00187";
        profile.City = "Roma";
        profile.Province = "RM";
        profile.VatRegistered = false;
        profile.SwissVatNumber = null;
        profile.QrIban = null;
        profile.Iban = null;
        var owner = await rig.Db.PropertyOwners.SingleAsync(o => o.Id == rig.OwnerId);
        owner.FiscalCode = "RSSMRA70A01H501W";
        owner.BillingPostalCode = "00100";
        owner.BillingCity = "Roma";
        owner.BillingProvince = "RM";
        owner.BillingCountry = "IT";
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        await InCountryAsync(rig, "IT");
    }

    /// <summary>A completed cleaning request of the lake house of the rig, with the answer and the rate given.</summary>
    private static async Task<Guid> AnsweredRequestAsync(OwnerInvoiceRig rig, CleaningSupplierVat? answer, decimal? rate)
    {
        var request = new ServiceRequest
        {
            OrgId = rig.OrgId,
            PropertyId = rig.LakeHouse,
            SupplierOrgId = Guid.NewGuid(),
            RentalContext = ServiceRequestRentalContext.ShortRent,
            Category = ServiceCategories.Cleaning,
            Status = ServiceRequestStatus.Completato,
            CompletedAt = At(10, 20),
            FinalAmountCents = 10000,
            SupplierVat = answer,
            SupplierVatRatePercent = rate,
            SupplierVatAnsweredAt = answer is null ? null : At(10, 25),
            SupplierVatAnsweredByUserId = answer is null ? null : Agency,
            CreatedAt = At(10, 1),
            UpdatedAt = At(10, 20),
        };
        rig.Db.ServiceRequests.Add(request);
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();
        return request.Id;
    }

    /// <summary>The ledger line of the recharge of a cleaning request of 100.00, as the posting writes it.</summary>
    private static OwnerLedgerEntry CleaningLine(OwnerInvoiceRig rig, Guid request) =>
        rig.Line(
            OwnerLedgerEntryType.OwnerCharge,
            -100.00m,
            rig.LakeHouse,
            OwnerLedgerSourceType.CleaningRequest,
            request,
            OwnerLedgerCleaningRecharges.ReferenceOf(request),
            "Pulizia del 20/10/2026");

    /// <summary>The row of the invoice of a cleaning request (its description names the reference of the request).</summary>
    private static FiscalDocumentLine RowOf(FiscalDocument invoice, Guid request) =>
        Assert.Single(invoice.Lines, l => l.Description.Contains(OwnerLedgerCleaningRecharges.ReferenceOf(request), StringComparison.Ordinal));

    // ---------------------------------------------------------------- the endpoint

    /// <summary>The endpoint over the world, for a caller of <paramref name="scope"/> who may (or may not) write the property of the request.</summary>
    private static ServiceRequestCleaningVatController Controller(World world, HostScope scope, bool mayWrite)
    {
        var userId = scope.GrantedToUserId ?? Agency;
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(scope.OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(userId, It.IsAny<IReadOnlySet<string>>(), scope.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync((ClaimsPrincipal _, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
                resource is HostResource
                && (mayWrite || requirements.All(r => r is not HostOperationRequirement { PermissionKey: "property.write" }))
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failed());

        return new ServiceRequestCleaningVatController(world.Vat, authorization.Object, orgs.Object, scopes.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], "test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static string? PolicyOf(string action) =>
        typeof(ServiceRequestCleaningVatController).GetMethod(action)!.GetCustomAttribute<AuthorizeAttribute>()?.Policy;

    /// <summary>
    /// A Swiss agency with its owners, Ticino properties, stays and cleaning requests of companies, the owner accounting with the real cost source of the cleanings and the
    /// service of the «Da fare», the mandates and the question of the VAT over one in-memory database.
    /// </summary>
    private sealed class World
    {
        private World(AppDbContext db)
        {
            Db = db;
            Clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero));
            var options = Microsoft.Extensions.Options.Options.Create(new OwnerAccountingOptions());
            Fiscal = new OwnerFiscalProfileService(db, Clock, NullLogger<OwnerFiscalProfileService>.Instance);
            Activities = new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock);
            Accounting = new OwnerAccountingService(
                db,
                Fiscal,
                Clock,
                NullLogger<OwnerAccountingService>.Instance,
                options,
                cleaningCosts: new CleaningCostSource(db),
                activities: Activities);
            Mandates = new OwnerMandateService(db, Clock, options, NullLogger<OwnerMandateService>.Instance);
            Vat = new CleaningVatService(db, Activities, Clock, NullLogger<CleaningVatService>.Instance);
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public OwnerFiscalProfileService Fiscal { get; }

        public InternalActivityService Activities { get; }

        public OwnerAccountingService Accounting { get; }

        public OwnerMandateService Mandates { get; }

        public CleaningVatService Vat { get; }

        public static World Create() => new(
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"CleaningVat_{Guid.NewGuid()}").Options,
                new CleaningVatTenant(),
                new EphemeralDataProtectionProvider()));

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

        /// <summary>A Ticino property in francs (country CH) owned from January by the owner alone, of the org given (this one by default).</summary>
        public async Task<Guid> PropertyAsync(Guid owner, string name, Guid? orgId = null)
        {
            var org = orgId ?? OrgId;
            var property = new Property { OrgId = org, OwnerId = Agency, Name = name, Address = "Via 1", City = "Lugano", Country = "CH", Currency = "CHF" };
            Db.Properties.Add(property);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = org,
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
            var property = new Property { OrgId = OrgId, OwnerId = Agency, Name = name, Address = "Via 1", City = "Lugano", Country = "CH", Currency = "CHF" };
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

        /// <summary>A mandate of the owner from January: no commission, the cleaning recharged at its cost.</summary>
        public Task<OwnerMandate> MandateAsync(Guid owner) =>
            Mandates.CreateVersionAsync(
                OrgWide,
                owner,
                new OwnerMandateCommand { ValidFrom = new DateOnly(2026, 1, 1), CommissionPercent = 0m, CleaningRechargeMode = CleaningRechargeMode.ActualCost },
                null);

        /// <summary>A confirmed stay of the property, priced by hand.</summary>
        public async Task<Guid> StayAsync(Guid property, DateTime checkIn, DateTime checkOut)
        {
            var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
            var booking = new Booking
            {
                OrgId = OrgId,
                PropertyId = property,
                GuestId = guest.Id,
                CheckInDate = checkIn.Date,
                CheckOutDate = checkOut.Date,
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

        /// <summary>A completed cleaning request of the company for the property, with its final amount, written straight to the table as the cost source reads it.</summary>
        public async Task<Guid> RequestAsync(
            Guid property,
            Guid company,
            DateTime completedAt,
            int cents,
            Guid? stay = null,
            Guid? orgId = null,
            Action<ServiceRequest>? change = null)
        {
            var request = new ServiceRequest
            {
                OrgId = orgId ?? OrgId,
                PropertyId = property,
                BookingId = stay,
                SupplierOrgId = company,
                RentalContext = ServiceRequestRentalContext.ShortRent,
                Category = ServiceCategories.Cleaning,
                Status = ServiceRequestStatus.Completato,
                CompletedAt = completedAt,
                FinalAmountCents = cents,
                CreatedAt = completedAt.AddDays(-3),
                UpdatedAt = completedAt,
            };
            change?.Invoke(request);
            Db.ServiceRequests.Add(request);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return request.Id;
        }

        /// <summary>The request as it is stored.</summary>
        public Task<ServiceRequest> RequestRowAsync(Guid id) => Db.ServiceRequests.AsNoTracking().SingleAsync(r => r.Id == id);

        /// <summary>The version of October of the owner, generated and approved.</summary>
        public async Task ApprovedVersionAsync(Guid owner)
        {
            Clock.Advance(TimeSpan.FromMinutes(1));
            var version = await Accounting.GenerateVersionAsync(OrgId, owner, 2026, 10, Agency);
            Assert.Equal(OwnerStatementVersionStatus.Draft, version.Status);
            await Accounting.ApproveVersionAsync(OrgId, version.Id, Agency);
            Db.ChangeTracker.Clear();
        }

        /// <summary>The «Da fare» rows (the closed ones too), oldest first.</summary>
        public async Task<List<InternalActivity>> TodosAsync() =>
            await Db.InternalActivities.AsNoTracking().Where(a => a.Action != null).OrderBy(a => a.CreatedAt).ToListAsync();
    }
}

file sealed class CleaningVatTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
