using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using AuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;
using CasazenPolicies = Casazen.Web.Authorization.CasazenPolicies;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using FeatureGateAttribute = Casazen.Web.Infrastructure.FeatureGateAttribute;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E14/F01/US01/T01 (NF-114, D9, ⚖ R4), after the review: the credit note of an owner invoice gives the VAT the invoice posted back to the owner ledger, once and
/// to the cent (the whole invoice, partial notes, a closed month, a mandate with the VAT included), and it is made only from the owner invoices
/// (<c>agency.statement.manage</c>): the endpoints of the guest documents (<c>payment.write</c>, held by the property manager) refuse an owner invoice.
/// </summary>
public class OwnerInvoiceCreditNoteTests
{
    /// <summary>
    /// PROOF TEST: money rule. A credit note of the whole invoice posts the reversal of the line of the VAT (the owner ledger is the commission alone again), the
    /// invoice is credited in full and a second note is refused without posting anything; the month is free for a new invoice, whose VAT is posted once more.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_CreditNoteGivesTheVatBack_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);
        var invoice = await IssueInvoiceAsync(rig, version);
        Assert.Equal(-81.00m, (await VatLinesAsync(rig)).Sum(e => e.Amount));
        Assert.Equal(-1081.00m, await LedgerTotalAsync(rig));

        var note = await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, null, "TEST storno", confirm: true, "auth0|agency");

        Assert.Equal((DocumentKind.CreditNote, FiscalDocumentStatus.Issued, "PM2026/2", (Guid?)invoice.Id), (note.Kind, note.Status, note.Number, note.CreditedDocumentId));
        Assert.Equal((1000.00m, 81.00m, 1081.00m), (note.TaxableBase, note.VatAmount, note.Total));
        Assert.True((await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == invoice.Id)).CreditedInFull);

        // The VAT line is reversed, once: the posted line and its reversal, nothing else.
        var lines = await VatLinesAsync(rig);
        var posted = Assert.Single(lines, e => e.ReversesEntryId is null);
        var reversal = Assert.Single(lines, e => e.ReversesEntryId is not null);
        Assert.Equal(((Guid?)posted.Id, 81.00m, 2026, 10), (reversal.ReversesEntryId, reversal.Amount, reversal.PeriodYear, reversal.PeriodMonth));
        Assert.Equal(0m, lines.Sum(e => e.Amount));
        Assert.Equal(-1000.00m, await LedgerTotalAsync(rig));

        // The draft version of the month was generated again with the VAT given back.
        var current = await CurrentVersionAsync(rig);
        Assert.Equal((-1000.00m, -1000.00m), (current.ClosingBalance, current.Commission));

        // Nothing is left to credit: a second note is refused and posts nothing.
        var second = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, null, "TEST", confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.CreditAmountExceeds, second.Code);
        Assert.Equal(2, (await VatLinesAsync(rig)).Count);

        // An invoice credited in full frees its owner and month: the new invoice posts its VAT once more.
        var reissued = await IssueInvoiceAsync(rig, current);
        Assert.NotEqual(invoice.Id, reissued.Id);
        Assert.Equal("PM2026/3", reissued.Number);
        Assert.Equal(-81.00m, (await VatLinesAsync(rig)).Sum(e => e.Amount));
        Assert.Equal(3, (await VatLinesAsync(rig)).Count);
    }

    /// <summary>
    /// PROOF TEST: money rule. A partial note gives back the VAT it carries (a new line, the line of the invoice is left alone), never more than what is left;
    /// the note that completes the credit gives back all that is left, so the cents of the partial notes are not lost: the VAT of the invoice is given back
    /// to the cent, never more.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_PartialCreditNotes_GiveTheVatBackOnceAndNeverMore_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);
        var invoice = await IssueInvoiceAsync(rig, version);

        var first = await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, 100.00m, "TEST parziale", confirm: true, null);
        var second = await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, 100.00m, "TEST parziale", confirm: true, null);

        // 100.00 of 1081.00 at 8.1%: 92.51 and 7.49 of VAT; the invoice is not credited in full yet.
        Assert.Equal((7.49m, 7.49m), (first.VatAmount, second.VatAmount));
        Assert.False((await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == invoice.Id)).CreditedInFull);
        var lines = await VatLinesAsync(rig);
        Assert.Equal(3, lines.Count);
        Assert.Equal(7.49m, Assert.Single(lines, l => l.SourceId == first.Id).Amount);
        Assert.Equal(7.49m, Assert.Single(lines, l => l.SourceId == second.Id).Amount);
        Assert.All(lines.Where(l => l.SourceId != invoice.Id), l => Assert.Null(l.ReversesEntryId));
        Assert.Equal(-81.00m + 7.49m + 7.49m, lines.Sum(l => l.Amount));

        // More than what is left is refused and posts nothing.
        var tooMuch = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, 881.01m, "TEST", confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.CreditAmountExceeds, tooMuch.Code);
        Assert.Equal(3, (await VatLinesAsync(rig)).Count);

        // The rest of the invoice (881.00) carries 66.01 of VAT, but the note that completes the credit gives back what is left of the 81.00: 66.02.
        var last = await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, null, "TEST resto", confirm: true, null);
        Assert.Equal((881.00m, 66.01m), (last.Total, last.VatAmount));
        Assert.True((await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == invoice.Id)).CreditedInFull);
        lines = await VatLinesAsync(rig);
        Assert.Equal(4, lines.Count);
        Assert.Equal(66.02m, Assert.Single(lines, l => l.SourceId == last.Id).Amount);
        Assert.Equal(0m, lines.Sum(l => l.Amount));
    }

    /// <summary>
    /// PROOF TEST: money rule. When the month of the invoice is closed for the owner the reversal of its VAT goes in the month of today (the closed month is never
    /// written to), once.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_CreditNoteOfAClosedMonth_GivesTheVatBackInTheMonthOfToday_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);
        var invoice = await IssueInvoiceAsync(rig, version);
        var row = await rig.Db.OwnerClosePeriods.FirstOrDefaultAsync(p => p.PropertyOwnerId == rig.OwnerId && p.PeriodYear == 2026 && p.PeriodMonth == 10);
        if (row is null)
        {
            row = new OwnerClosePeriod { OrgId = rig.OrgId, PropertyOwnerId = rig.OwnerId, PeriodYear = 2026, PeriodMonth = 10, CreatedAt = OwnerInvoiceRig.Now.UtcDateTime };
            rig.Db.OwnerClosePeriods.Add(row);
        }

        row.Status = OwnerClosePeriodStatus.Closed;
        row.ClosedAt = OwnerInvoiceRig.Now.UtcDateTime;
        await rig.Db.SaveChangesAsync();

        await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, null, "TEST storno", confirm: true, null);

        var lines = await VatLinesAsync(rig);
        var posted = Assert.Single(lines, e => e.ReversesEntryId is null);
        var reversal = Assert.Single(lines, e => e.ReversesEntryId is not null);
        Assert.Equal((2026, 10, -81.00m), (posted.PeriodYear, posted.PeriodMonth, posted.Amount));
        Assert.Equal(((Guid?)posted.Id, 2026, 11, 81.00m), (reversal.ReversesEntryId, reversal.PeriodYear, reversal.PeriodMonth, reversal.Amount));
        Assert.Equal(0m, lines.Sum(e => e.Amount));
    }

    /// <summary>
    /// PROOF TEST: money rule. A mandate whose amounts include the VAT (⚖ R4) makes the invoice take the VAT out of the ledger amounts and posts none, so its
    /// credit note has nothing to give back and posts nothing either.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_MandateWithTheVatIncluded_PostsNoVat_AndItsCreditNoteGivesNothingBack_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        rig.Db.OwnerMandates.Add(new OwnerMandate
        {
            OrgId = rig.OrgId,
            PropertyOwnerId = rig.OwnerId,
            CommissionPercent = 10m,
            CommissionIncludesVat = true,
            MandatePdfUrl = "mandates/test.pdf",
            ValidFrom = new DateOnly(2026, 1, 1),
            CreatedAt = OwnerInvoiceRig.Now.UtcDateTime,
        });
        await rig.Db.SaveChangesAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);

        var invoice = await IssueInvoiceAsync(rig, version);

        // 1000.00 is the gross: the invoice takes 74.93 of VAT out of it; the ledger gets no line of VAT.
        Assert.Equal((925.07m, 74.93m, 1000.00m), (invoice.TaxableBase, invoice.VatAmount, invoice.Total));
        Assert.Empty(await VatLinesAsync(rig));

        var note = await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, null, "TEST storno", confirm: true, null);

        Assert.Equal((DocumentKind.CreditNote, 1000.00m), (note.Kind, note.Total));
        Assert.Empty(await VatLinesAsync(rig));
        Assert.Equal(-1000.00m, await LedgerTotalAsync(rig));
    }

    /// <summary>
    /// PROOF TEST: permission rule. The property manager holds <c>payment.write</c> and no agency permission (DEB-01): the endpoints of the guest documents, open to
    /// whoever holds <c>payment.write</c> and reaches the whole org, refuse an owner invoice (make it again, issue it, credit it, discard it: 422
    /// <c>document_owner_invoice</c>) and write nothing, while its PDF stays readable there. The credit note and the discard are on <c>api/owner-invoices</c> behind
    /// <c>agency.statement.manage</c> and its flag, of the org of the invoice only.
    /// </summary>
    [Fact]
    public async Task OwnerInvoice_PropertyManagerCannotCreditOrDiscardAnOwnerInvoice_Test()
    {
        // The roles: the property manager of the short-rent area writes payments, but no agency permission is its own; the owner and the administrator hold it.
        var propertyManager = OrgRoleCatalog.SeededRoles.Single(r => r.ContextKey == OrgRoleCatalog.ShortRent && r.RoleKey == OrgRoleCatalog.PropertyManagerRoleKey);
        Assert.Contains("payment.write", propertyManager.Permissions);
        Assert.DoesNotContain(AgencyPermissions.StatementManage, OrgRoleCatalog.AgencyPermissionsOf(OrgRole.PropertyManager));
        Assert.DoesNotContain(AgencyPermissions.StatementManage, OrgRoleCatalog.AgencyPermissionsOf(OrgRole.Accountant));
        Assert.Contains(AgencyPermissions.StatementManage, OrgRoleCatalog.AgencyPermissionsOf(OrgRole.Owner));
        Assert.Contains(AgencyPermissions.StatementManage, OrgRoleCatalog.AgencyPermissionsOf(OrgRole.Admin));

        // The owner invoices: both actions need agency.statement.manage, the whole controller its flag.
        var controller = typeof(OwnerInvoicesController);
        Assert.Equal(new[] { FeatureFlags.OwnerInvoicing }, GatesOf(controller));
        Assert.All(
            new[] { nameof(OwnerInvoicesController.CreditNote), nameof(OwnerInvoicesController.Discard) },
            action => Assert.Contains(
                CasazenPolicies.AgencyStatementManage,
                controller.GetMethod(action)!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>().Select(a => a.Policy)));

        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);
        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);
        var asPropertyManager = GuestDocumentsOf(rig);

        // The draft: the generic endpoints neither make it again, nor issue it, nor discard it.
        await AssertUseOwnerInvoicesAsync(() => asPropertyManager.Refresh(draft.Id));
        await AssertUseOwnerInvoicesAsync(() => asPropertyManager.Issue(draft.Id, new IssueFiscalDocumentRequest { Confirm = true }));
        await AssertUseOwnerInvoicesAsync(() => asPropertyManager.Discard(draft.Id));
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync()).Status);

        // The owner invoices discard it, of the org of the invoice only; the month is free for a new draft.
        await Assert.ThrowsAsync<NotFoundException>(() => rig.Invoices.DiscardDraftAsync(Guid.NewGuid(), draft.Id));
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync()).Status);
        await rig.Invoices.DiscardDraftAsync(rig.OrgId, draft.Id);
        Assert.Equal(FiscalDocumentStatus.Discarded, (await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync()).Status);
        var again = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);
        Assert.NotEqual(draft.Id, again.Id);

        // The issued invoice: the generic credit note is refused and writes nothing (no note, no line in the ledger); the document is still read there.
        var issued = await rig.Invoices.IssueAsync(rig.OrgId, again.Id, confirm: true, null);
        var documents = await rig.Db.FiscalDocuments.AsNoTracking().CountAsync();
        var entries = await rig.Db.OwnerLedgerEntries.AsNoTracking().CountAsync();
        await AssertUseOwnerInvoicesAsync(() => asPropertyManager.CreditNote(issued.Id, new CreateCreditNoteRequest { Reason = "TEST", Confirm = true }));
        Assert.Equal((documents, entries), (await rig.Db.FiscalDocuments.AsNoTracking().CountAsync(), await rig.Db.OwnerLedgerEntries.AsNoTracking().CountAsync()));
        Assert.False((await rig.Db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == issued.Id)).CreditedInFull);
        Assert.IsType<OkObjectResult>((await asPropertyManager.Get(issued.Id)).Result);

        // The owner invoices credit it (the confirmation is needed, the org of the invoice only), and an issued invoice is no draft to discard.
        var unconfirmed = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Invoices.CreateCreditNoteAsync(rig.OrgId, issued.Id, null, "TEST", confirm: false, null));
        Assert.Equal(FiscalDocumentErrors.ConfirmationRequired, unconfirmed.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => rig.Invoices.CreateCreditNoteAsync(Guid.NewGuid(), issued.Id, null, "TEST", confirm: true, null));
        var notDraft = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Invoices.DiscardDraftAsync(rig.OrgId, issued.Id));
        Assert.Equal(FiscalDocumentErrors.NotDraft, notDraft.Code);
        Assert.Equal(entries, await rig.Db.OwnerLedgerEntries.AsNoTracking().CountAsync());
        var note = await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, issued.Id, null, "TEST storno", confirm: true, null);
        Assert.Equal(DocumentKind.CreditNote, note.Kind);
        Assert.Equal(entries + 1, await rig.Db.OwnerLedgerEntries.AsNoTracking().CountAsync());
    }

    private static async Task<FiscalDocument> IssueInvoiceAsync(OwnerInvoiceRig rig, OwnerStatementVersion version)
    {
        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, "auth0|agency");
        return await rig.Invoices.IssueAsync(rig.OrgId, draft.Id, confirm: true, "auth0|agency");
    }

    private static Task<List<OwnerLedgerEntry>> VatLinesAsync(OwnerInvoiceRig rig) =>
        rig.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.EntryType == OwnerLedgerEntryType.AgencyVat).ToListAsync();

    private static Task<decimal> LedgerTotalAsync(OwnerInvoiceRig rig) => rig.Db.OwnerLedgerEntries.AsNoTracking().SumAsync(e => e.Amount);

    private static Task<OwnerStatementVersion> CurrentVersionAsync(OwnerInvoiceRig rig) =>
        rig.Db.OwnerStatementVersions.AsNoTracking().SingleAsync(v => v.Status != OwnerStatementVersionStatus.Superseded);

    private static async Task AssertUseOwnerInvoicesAsync<T>(Func<Task<T>> action)
    {
        var refused = await Assert.ThrowsAsync<DomainRuleException>(action);
        Assert.Equal(OwnerInvoiceErrors.UseOwnerInvoices, refused.Code);
    }

    /// <summary>The endpoints of the guest documents as the property manager reaches them: signed in, the whole org, no booking to authorize.</summary>
    private static FiscalDocumentsController GuestDocumentsOf(OwnerInvoiceRig rig)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rig.OrgId);
        var scope = new Mock<IHostScopeResolver>();
        scope.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), rig.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostScope(rig.OrgId));
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|property-manager"), new Claim(ClaimTypes.NameIdentifier, "auth0|property-manager")], "test");
        return new FiscalDocumentsController(
            rig.Documents,
            Mock.Of<IHostResourceLookup>(),
            Mock.Of<IAuthorizationService>(),
            org.Object,
            scope.Object,
            NullLogger<FiscalDocumentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }

    private static List<string> GatesOf(System.Reflection.MemberInfo member) =>
        [.. member.GetCustomAttributes(typeof(FeatureGateAttribute), inherit: false).Cast<FeatureGateAttribute>().Select(gate => gate.Flag)];
}
