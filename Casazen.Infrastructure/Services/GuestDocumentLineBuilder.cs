using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestDocumentLineBuilder"/>
/// <remarks>
/// Reads the booking, its payments and its tourist tax records through the tenant filter of its caller; never writes. The descriptions of the lines are fixed words of the
/// document (<c>SharedResources</c>), never personal data.
/// </remarks>
public sealed class GuestDocumentLineBuilder(
    AppDbContext db,
    IDocumentTexts texts,
    IOptions<FiscalDocumentRulesOptions> documentOptions,
    IOptions<SwissTouristTaxOptions> swissTouristTax) : IGuestDocumentLineBuilder
{
    /// <summary>The descriptions of the lines: fixed words of the document, never personal data.</summary>
    private const string LodgingKey = "FiscalDocLineLodging";
    private const string CleaningKey = "FiscalDocLineCleaning";
    private const string TouristTaxKey = "FiscalDocLineTouristTax";
    private const string SwissTouristTaxKey = "FiscalDocLineTouristTaxCh";

    public IReadOnlyList<BookingSource> PlatformCollectedChannelsOf(OrgFiscalIssuerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.PlatformCollectedTouristTaxChannelsConfirmed)
            return profile.PlatformCollectedTouristTaxChannels;

        return FiscalDocumentRules.IsSwiss(profile.CountryCode) ? swissTouristTax.Value.PlatformCollected.Distinct().ToList() : [];
    }

    public async Task<GuestDocumentLines> BuildAsync(
        Guid bookingId,
        FiscalTaxRule rule,
        IReadOnlyList<BookingSource> platformCollectedTouristTaxChannels,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(platformCollectedTouristTaxChannels);

        var stay = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new StayFacts(
                b.Id,
                b.Source,
                b.BasePrice,
                b.CleaningFee,
                b.TouristTax,
                b.NumberOfGuests,
                b.CheckInDate,
                b.CheckOutDate,
                b.OtaCompanyCollect,
                b.OtaCommissionIncludedInPayments,
                b.Property.SwissMunicipalityCode,
                b.Property.ComuneIstatCode))
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Booking {bookingId} was not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var language = DocumentLanguages.Italian;
        var (lodging, cleaning) = SplitOf(stay.BasePrice, stay.CleaningFee);
        var lines = new List<FiscalDocumentGrossLine>();
        if (lodging > 0m)
            lines.Add(Line(texts.Get(LodgingKey, language, stay.Nights), lodging, FiscalLineType.Accommodation, rule));
        if (cleaning > 0m)
            lines.Add(Line(texts.Get(CleaningKey, language), cleaning, FiscalLineType.Cleaning, rule));

        var tax = rule.IsSwiss
            ? await SwissTouristTaxAsync(stay, platformCollectedTouristTaxChannels, ct)
            : await ItalianTouristTaxAsync(stay, ct);
        if (tax > 0m)
            lines.Add(Line(texts.Get(rule.IsSwiss ? SwissTouristTaxKey : TouristTaxKey, language), tax, FiscalLineType.TouristTax, rule));

        return new GuestDocumentLines(lines, await ChannelOfAsync(stay, ct));
    }

    /// <summary>
    /// The stay and the cleaning of a price that includes the cleaning (<c>BasePrice</c>): the cleaning is at most the price, and the two add up to it, so the cleaning is never
    /// billed twice. A booking of a portal has no cleaning fee of its own: its whole price is the stay.
    /// </summary>
    internal static (decimal Lodging, decimal Cleaning) SplitOf(decimal basePrice, decimal cleaningFee)
    {
        var price = Math.Max(basePrice, 0m);
        var cleaning = Math.Clamp(cleaningFee, 0m, price);
        return (price - cleaning, cleaning);
    }

    /// <summary>
    /// Who collected the money of a booking: for a portal, the reservation (<c>companyCollect</c>, then whether the commission is taken out of the payments), then the configured
    /// default of the portal; for any other booking, CasaZen when a payment went through it (Stripe), else outside CasaZen.
    /// </summary>
    internal static FiscalPaymentChannel ChannelOf(
        BookingSource source,
        bool? companyCollect,
        bool? commissionIncludedInPayments,
        IReadOnlyCollection<BookingSource> portalCollectedByDefault,
        bool paidThroughCasaZen)
    {
        if (FiscalCopy.IsOtaBookingSource(source))
        {
            var portalCollects = companyCollect ?? commissionIncludedInPayments ?? portalCollectedByDefault.Contains(source);
            return portalCollects ? FiscalPaymentChannel.Portal : FiscalPaymentChannel.Outside;
        }

        return paidThroughCasaZen ? FiscalPaymentChannel.CasaZen : FiscalPaymentChannel.Outside;
    }

    private async Task<FiscalPaymentChannel> ChannelOfAsync(StayFacts stay, CancellationToken ct)
    {
        var paidThroughCasaZen = !FiscalCopy.IsOtaBookingSource(stay.Source)
                                 && await db.Payments
                                     .AsNoTracking()
                                     .AnyAsync(
                                         p => p.BookingId == stay.Id
                                              && p.Status != PaymentStatus.Failed
                                              && p.Status != PaymentStatus.Canceled
                                              && (p.StripePaymentIntentId != null || p.TransactionId.StartsWith("pi_")),
                                         ct);
        return ChannelOf(
            stay.Source,
            stay.OtaCompanyCollect,
            stay.OtaCommissionIncludedInPayments,
            documentOptions.Value.PortalCollectedChannels,
            paidThroughCasaZen);
    }

    /// <summary>
    /// Italy: the tax of the stay the host confirmed guest by guest (<see cref="StayTouristTax"/>), else the amount of the booking, and only when the host or the agency collects
    /// it: the channel rule of the comune decides for a portal, then the declaration of the host; a collector nobody knows yet gives no line (never a charge the portal may
    /// have made already).
    /// </summary>
    private async Task<decimal> ItalianTouristTaxAsync(StayFacts stay, CancellationToken ct)
    {
        var istatCode = stay.ComuneIstatCode?.Trim();
        TouristTaxChannelRule? channelRule = null;
        if (TouristTaxCollectorRules.IsChannelBooking(stay.Source) && !string.IsNullOrEmpty(istatCode))
        {
            channelRule = await db.TouristTaxChannelRules
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Channel == stay.Source && r.IstatCode == istatCode && r.IsActive, ct);
        }

        var record = await db.StayTouristTaxes
            .AsNoTracking()
            .Where(t => t.BookingId == stay.Id)
            .Select(t => new TaxRecord(t.ConfirmedAt, t.Total, t.DeclaredCollector))
            .FirstOrDefaultAsync(ct);
        var decision = TouristTaxCollectorRules.Decide(stay.Source, channelRule, record?.DeclaredCollector);
        if (decision.Collector != TouristTaxCollector.Host)
            return 0m;

        return record is { ConfirmedAt: not null } ? record.Total : stay.TouristTax;
    }

    /// <summary>
    /// Switzerland: nothing for a channel the profile says collects the tax itself; the amount of the booking when it has one; else the rule of the canton of the property
    /// (<c>SwissMunicipalities</c>) for the guests and the nights of the stay.
    /// </summary>
    private async Task<decimal> SwissTouristTaxAsync(StayFacts stay, IReadOnlyList<BookingSource> platformCollected, CancellationToken ct)
    {
        if (platformCollected.Contains(stay.Source))
            return 0m;

        if (stay.TouristTax > 0m)
            return stay.TouristTax;

        var municipality = stay.SwissMunicipalityCode?.Trim();
        if (string.IsNullOrEmpty(municipality))
            return 0m;

        var canton = await db.SwissMunicipalities
            .AsNoTracking()
            .Where(m => m.MunicipalityCode == municipality)
            .Select(m => m.CantonCode)
            .FirstOrDefaultAsync(ct);
        return swissTouristTax.Value.TaxOf(canton, DateOnly.FromDateTime(stay.CheckIn), stay.Nights, stay.NumberOfGuests) ?? 0m;
    }

    private static FiscalDocumentGrossLine Line(string description, decimal gross, FiscalLineType type, FiscalTaxRule rule)
    {
        var rate = rule.RateOf(type);
        return new FiscalDocumentGrossLine(description, 1m, gross, rate.Percent, rate.NatureCode, type);
    }

    private sealed record TaxRecord(DateTime? ConfirmedAt, decimal Total, TouristTaxCollector? DeclaredCollector);

    private sealed record StayFacts(
        Guid Id,
        BookingSource Source,
        decimal BasePrice,
        decimal CleaningFee,
        decimal TouristTax,
        int NumberOfGuests,
        DateTime CheckIn,
        DateTime CheckOut,
        bool? OtaCompanyCollect,
        bool? OtaCommissionIncludedInPayments,
        string? SwissMunicipalityCode,
        string? ComuneIstatCode)
    {
        /// <summary>The nights of the stay (calendar days between check-in and check-out).</summary>
        public int Nights => Math.Max(0, (CheckOut.Date - CheckIn.Date).Days);
    }
}
