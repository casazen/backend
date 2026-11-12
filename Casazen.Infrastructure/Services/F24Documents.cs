using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The PDF content of the F24 data of a month (NF-25, ADR-018, E06/F01/US01/T02), in the style of <see cref="FiscalReportDocuments"/>: the stored
/// <see cref="F24Record"/> as it is (tax code, period, amount, due date, payment), with the labels of the resource files. It is the data to fill the
/// form with, never the form that was filed: the file says so on top of its first page.
/// </summary>
public static class F24Documents
{
    public static PdfDocumentContent Pdf(F24Record record, string orgName, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(texts);

        var period = $"{record.ReferenceMonth.ToString("00", CultureInfo.InvariantCulture)}/{record.ReferenceYear.ToString(CultureInfo.InvariantCulture)}";
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph(texts.Get("F24DocNotFiled", language), Bold: true),
            new PdfParagraph(texts.Get("F24DocOrg", language, orgName)),
            new PdfParagraph(texts.Get("F24DocPeriod", language, period)),
            new PdfHeading(texts.Get("F24DocSectionErario", language)),
            new PdfTable(
                [
                    new PdfTableColumn(texts.Get("F24DocTaxCode", language), 1.1),
                    new PdfTableColumn(texts.Get("F24DocColMonth", language), 1.1),
                    new PdfTableColumn(texts.Get("F24DocColYear", language), 1.1),
                    new PdfTableColumn(texts.Get("F24DocAmount", language), 1.4, PdfCellAlignment.Right),
                ],
                [
                    [
                        record.TaxCode,
                        record.ReferenceMonth.ToString("00", CultureInfo.InvariantCulture),
                        record.ReferenceYear.ToString(CultureInfo.InvariantCulture),
                        DocumentLanguages.Money(record.TotalAmount, language),
                    ],
                ]),
            new PdfParagraph(texts.Get("F24DocDueDate", language, DocumentLanguages.Day(DateOnly.FromDateTime(record.DueDate)))),
            new PdfParagraph(record.PaidAt is { } paid
                ? texts.Get("F24DocPaidAt", language, DocumentLanguages.Day(DateOnly.FromDateTime(paid)))
                : texts.Get("F24DocNotPaid", language)),
            new PdfParagraph(texts.Get("F24DocDisclaimer", language)),
        };

        return new PdfDocumentContent(texts.Get("F24DocTitle", language, period), blocks)
        {
            Language = DocumentLanguages.Normalize(language) == DocumentLanguages.English ? "en-GB" : "it-IT",
        };
    }
}
