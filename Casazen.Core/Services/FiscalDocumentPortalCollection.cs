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
/// <see cref="FiscalLineType.TouristTax"/> line is a tax the guest still owes at the property: the portal was paid the rest. The document is declared paid through the portal for what
/// the portal collected, never for that line (⚖ product owner: the prudent default), unless the collection of the stay (E25, <see cref="StayTouristTax"/>) records the tax as collected.</item>
/// <item><b>What E25 records.</b> The tax the guest paid online (<see cref="StayTouristTax.CollectedAmount"/> net of <see cref="StayTouristTax.RefundedAmount"/>) and the tax the host recorded
/// paid at the property (<see cref="TouristTaxCollectionStatus.PaidAtProperty"/>: all of it) are not «to collect»; what is not recorded, or recorded only in part, is. The record is read by the
/// readers of the document (<c>FiscalDocumentService</c>, one reading for the API and the PDFs) into <see cref="FiscalDocument.TouristTaxCollected"/>; with no record it is zero.</item>
/// <item><b>Derived, never stored.</b> From the lines, the total, who collected the money (<see cref="FiscalDocument.PaymentChannel"/>) and the record of E25: no column, so a document issued
/// before this rule is read the same way, and the document keeps its lines, its total and its stored payment state.</item>
/// <item><b>Only the guest document of a portal.</b> A document of any other channel, a credit note, an owner invoice, a self-billed invoice, a document CasaZen did not issue,
/// a portal document with no such line and one whose tax is all recorded as collected say what they said before: <see cref="SplitOf"/> gives <c>null</c>.</item>
/// </list>
/// </remarks>
public static class FiscalDocumentPortalCollection
{
    /// <summary>
    /// The part of <paramref name="document"/> the portal collected and the part of the tourist tax still to collect at the property, or <c>null</c> when nothing is left to collect: the document
    /// has no tourist tax line the portal left to the host (the document of another channel, a credit note, a document without a line of tourist tax), or the collection of the stay
    /// (<see cref="FiscalDocument.TouristTaxCollected"/>) records all of that tax as collected. Nothing changes for such a document.
    /// </summary>
    public static PortalCollectionSplit? SplitOf(FiscalDocument document)
    {
        var tax = TaxLeftToTheHost(document);
        var toCollect = tax - Math.Min(tax, Math.Max(document.TouristTaxCollected, 0m));
        return toCollect > 0m ? new PortalCollectionSplit(document.Total - tax, toCollect) : null;
    }

    /// <summary>
    /// The tourist tax the portal left to the host on <paramref name="document"/>: its lines of tourist tax (never more than the total), whatever E25 records of their collection; zero for
    /// every document that is not the guest document of a portal and for a portal document with no such line.
    /// </summary>
    public static decimal TaxLeftToTheHost(FiscalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!IsGuestDocumentOfAPortal(document) || document.Total <= 0m)
            return 0m;

        return Math.Min(document.Lines.Where(IsTouristTax).Sum(GrossOf), document.Total);
    }

    /// <summary>
    /// The part of the tourist tax of <paramref name="document"/> that the record of the collection of its stay (E25) says is collected: all of it once the host recorded it paid at the property
    /// (<see cref="TouristTaxCollectionStatus.PaidAtProperty"/>), else what the guest paid online net of the refunds (<paramref name="collectedOnline"/> less <paramref name="refundedOnline"/>),
    /// the same figure the collection itself reads; never more than the tax the document bills. Zero when the document has no tax left to the host.
    /// </summary>
    public static decimal CollectedOf(FiscalDocument document, TouristTaxCollectionStatus status, decimal collectedOnline, decimal refundedOnline)
    {
        var tax = TaxLeftToTheHost(document);
        if (tax <= 0m)
            return 0m;

        return status == TouristTaxCollectionStatus.PaidAtProperty ? tax : Math.Min(tax, Math.Max(0m, collectedOnline - refundedOnline));
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

/// <summary>The guest document of a booking of a portal that left a tourist tax to the host, and some of it is still to collect (<see cref="FiscalDocumentPortalCollection.SplitOf"/>).</summary>
/// <param name="PaidViaPortal">What the portal collected: the total of the document less its tourist tax. Zero when the document bills nothing but the tax.</param>
/// <param name="ToCollectAtTheProperty">
/// The tourist tax still to collect at the property: all of it, or what the collection of the stay does not record as collected. Never more than the total of the document. The two parts add up to the
/// total of the document unless part of the tax is recorded as collected: the rest of the total is that part.
/// </param>
public sealed record PortalCollectionSplit(decimal PaidViaPortal, decimal ToCollectAtTheProperty)
{
    /// <summary>Whether <paramref name="line"/> is the part to collect at the property: a line of tourist tax of the document this split was made for.</summary>
    public bool IsToCollect(FiscalDocumentLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return FiscalDocumentPortalCollection.IsTouristTax(line);
    }
}
