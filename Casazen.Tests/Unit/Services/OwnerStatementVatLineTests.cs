using System.Text;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E14/F01/US01/T01 met E13/F01/US02/T01 (NF-110, NF-114): the VAT an owner invoice adds on top of the commission and the recharges is a line of the owner ledger of its
/// own (<see cref="OwnerLedgerEntryType.AgencyVat"/>), and the statement of the owner has a part for it.
/// </summary>
public class OwnerStatementVatLineTests
{
    /// <summary>
    /// PROOF TEST: money rule. The VAT of the owner invoice, and the line that gives it back with the credit note, are in the part «Provvigione del PM» of the statement,
    /// named with the number of the invoice, in both languages and in the CSV: the parts add up to the totals the version stores (its commission counts the VAT; a line
    /// left out of the part would be read as a withholding and the file refused with 409 <c>owner_ledger_immutable</c>).
    /// </summary>
    [Fact]
    public async Task OwnerStatement_TheVatOfTheOwnerInvoice_IsInTheCommissionPartWithItsNumber_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);
        var draft = await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null);
        var invoice = await rig.Invoices.IssueAsync(rig.OrgId, draft.Id, confirm: true, null);
        var documents = DocumentsOf(rig);
        var texts = new ResxDocumentTexts();

        // The version generated again with the VAT: one line of VAT, in the commission part, with the number of the invoice; the parts give the totals it stores.
        var current = await CurrentVersionAsync(rig);
        var content = await documents.GetContentAsync(rig.OrgId, current.Id, "it");
        var vat = Assert.Single(content.Lines, l => l.Type == OwnerLedgerEntryType.AgencyVat);
        Assert.Equal((OwnerStatementSection.Commission, "PM2026/1", -81.00m, false), (vat.Section, vat.Reference, vat.Amount, vat.Reversal));
        Assert.Equal(OwnerStatementTotals.Of(current), content.Totals);
        Assert.Equal((-1081.00m, 0m, -1081.00m), (content.Totals.Commission, content.Totals.Withholding, content.Totals.ClosingBalance));

        var italian = TextOf(OwnerStatementLayout.Pdf(content, texts).Blocks);
        Assert.Contains("IVA sulla provvigione e sui riaddebiti (fattura n. PM2026/1)", italian, StringComparison.Ordinal);
        var english = TextOf(OwnerStatementLayout.Pdf(await documents.GetContentAsync(rig.OrgId, current.Id, "en"), texts).Blocks);
        Assert.Contains("VAT on the commission and the recharges (invoice no. PM2026/1)", english, StringComparison.Ordinal);
        Assert.Contains("IVA sulla provvigione e sui riaddebiti (fattura n. PM2026/1)", Encoding.UTF8.GetString(OwnerStatementLayout.Csv(content, texts)), StringComparison.Ordinal);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await documents.GetPdfAsync(rig.OrgId, current.Id, "it")).Content, 0, 4));

        // The credit note gives the VAT back: the line that reverses it is in the same part, and the version generated again still gives the totals it stores.
        await rig.Invoices.CreateCreditNoteAsync(rig.OrgId, invoice.Id, null, "TEST storno", confirm: true, null);
        var again = await CurrentVersionAsync(rig);
        var reversed = await documents.GetContentAsync(rig.OrgId, again.Id, "it");
        Assert.Equal(2, reversed.Lines.Count(l => l.Type == OwnerLedgerEntryType.AgencyVat && l.Section == OwnerStatementSection.Commission));
        Assert.Equal(OwnerStatementTotals.Of(again), reversed.Totals);
        Assert.Equal((-1000.00m, 0m), (reversed.Totals.Commission, reversed.Totals.Withholding));
        Assert.Contains("storno: IVA sulla provvigione e sui riaddebiti (fattura n. PM2026/1)", TextOf(OwnerStatementLayout.Pdf(reversed, texts).Blocks), StringComparison.Ordinal);
    }

    private static Task<OwnerStatementVersion> CurrentVersionAsync(OwnerInvoiceRig rig) =>
        rig.Db.OwnerStatementVersions.AsNoTracking().SingleAsync(v => v.Status != OwnerStatementVersionStatus.Superseded);

    /// <summary>The files of the statements over the database of the rig (the storage, the scan and the e-mail are not used by what is read here).</summary>
    private static OwnerStatementDocuments DocumentsOf(OwnerInvoiceRig rig) => new(
        rig.Db,
        new MigraDocPdfDocumentRenderer(),
        new ResxDocumentTexts(),
        new InMemoryFileStorage(),
        new OwnerFileContentScanner(),
        new TestFeatureFlags(FeatureFlags.AgencyTier),
        Mock.Of<IEmailQueue>(),
        new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.example.com" })),
        new InternalActivityService(rig.Db, NullLogger<InternalActivityService>.Instance, rig.Clock),
        rig.Clock,
        NullLogger<OwnerStatementDocuments>.Instance);

    private static string TextOf(IEnumerable<PdfBlock> blocks) => string.Join(
        '\n',
        blocks.SelectMany(block => block switch
        {
            PdfHeading heading => [heading.Text],
            PdfParagraph paragraph => [paragraph.Text],
            PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
            _ => Array.Empty<string>(),
        }));
}
