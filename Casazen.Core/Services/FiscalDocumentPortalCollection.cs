using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// What a portal collected of the guest document of one of its bookings, and the tourist tax it did not (FIX/BE-PORTAL-TAXLINE, the finding of the Swiss acceptance
/// scenario S025): the one place that derives it, read by the PDF in both layouts and by the document the API gives.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Why the line is the host's.</b> The line builder (<see cref="IGuestDocumentLineBuilder"/>) draws a line of tourist tax only when the portal does not collect it
/// (Switzerland: the channel is not in the list the issuer confirmed, <see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>; Italy: the rule of the comune and the
/// declaration of the host say the host collects, <c>TouristTaxCollectorRules</c>). So on the document of a booking whose portal collected the money, a
/// <see cref="FiscalLineType.TouristTax"/> line is a tax the guest still owes at the property: the portal was paid the rest, and nothing on the document records that anyone
/// collected the tax. The document is declared paid through the portal for what the portal collected, never for that line (⚖ product owner: the prudent default).</item>
/// <item><b>Derived, never stored.</b> From the lines, the total and who collected the money (<see cref="FiscalDocument.PaymentChannel"/>), all of them kept with the
/// document: no column, so a document issued before this rule is read the same way, and the document keeps its lines, its total and its stored payment state.</item>
/// <item><b>Only the guest document of a portal.</b> A document of any other channel, a credit note, an owner invoice, a self-billed invoice, a document CasaZen did not issue
/// and a portal document with no such line say what they said before: <see cref="SplitOf"/> gives <c>null</c>.</item>
/// </list>
/// </remarks>
public static class FiscalDocumentPortalCollection
{
    /// <summary>
    /// The part of <paramref name="document"/> the portal collected and the part to collect at the property, or <c>null</c> when the document has no tourist tax line the portal left to
    /// the host (the document of another channel, a credit note, a document without a line of tourist tax: nothing changes for it). The two parts add up to the total of the document.
    /// </summary>
    public static PortalCollectionSplit? SplitOf(FiscalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!IsGuestDocumentOfAPortal(document) || document.Total <= 0m)
            return null;

        var toCollect = Math.Min(document.Lines.Where(IsTouristTax).Sum(GrossOf), document.Total);
        return toCollect > 0m ? new PortalCollectionSplit(document.Total - toCollect, toCollect) : null;
    }

    /// <summary>A line of tourist tax that bills something: the tax the host collects for the comune or the canton, outside the VAT.</summary>
    internal static bool IsTouristTax(FiscalDocumentLine line) => line.LineType == FiscalLineType.TouristTax && GrossOf(line) > 0m;

    /// <summary>What the guest owes for a line: its net plus its VAT (a line outside the VAT has none, and a line written before the VAT was stored has no more than its net).</summary>
    private static decimal GrossOf(FiscalDocumentLine line) => line.TotalPrice + (line.VatAmount ?? 0m);

    private static bool IsGuestDocumentOfAPortal(FiscalDocument document) =>
        document.Source == FiscalDocumentSource.CasaZen
        && document.Purpose == FiscalDocumentPurpose.GuestStay
        && document.Kind != FiscalDocumentKind.CreditNote
        && document.PaymentChannel == FiscalPaymentChannel.Portal;
}

/// <summary>The guest document of a booking of a portal that left a tourist tax to the host (<see cref="FiscalDocumentPortalCollection.SplitOf"/>).</summary>
/// <param name="PaidViaPortal">What the portal collected: the total of the document less the tax. Zero when the document bills nothing but the tax.</param>
/// <param name="ToCollectAtTheProperty">The tourist tax the portal did not collect: the host collects it at the property. Never more than the total of the document.</param>
public sealed record PortalCollectionSplit(decimal PaidViaPortal, decimal ToCollectAtTheProperty)
{
    /// <summary>Whether <paramref name="line"/> is the part to collect at the property: a line of tourist tax of the document this split was made for.</summary>
    public bool IsToCollect(FiscalDocumentLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return FiscalDocumentPortalCollection.IsTouristTax(line);
    }
}
