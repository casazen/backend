using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The draft lines of the document of a booking, whatever its source (NF-83, NF-85, NF-112, NF-113, D3, D6, E12/F01/US01/T01): the one place that says what a guest
/// is billed for and who collected the money. The amounts are gross (VAT included) in the currency of the property; the VAT of each line is the one of the tax rule.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Accommodation.</b> <c>BasePrice</c> without <c>CleaningFee</c>: <c>BasePrice</c> is «nightly rate × nights + cleaning» for a booking made in CasaZen (direct, manual),
/// and the room price the portal reports for a booking of a channel, whose import never writes a cleaning fee. The cleaning is billed once, never inside the stay and again
/// as its own line. For a booking of a portal the amount is the price of the host, never the service fee the portal charges the guest.</item>
/// <item><b>Cleaning.</b> <c>CleaningFee</c> when it is more than zero. The model has no extras of a booking: no line of type <see cref="FiscalLineType.ExtraService"/> is made.</item>
/// <item><b>Tourist tax.</b> Italy: the confirmed <see cref="StayTouristTax"/> of the stay, else <c>Booking.TouristTax</c>, only when the host or the agency collects it
/// (<see cref="Casazen.Core.TouristTax.TouristTaxCollectorRules"/>: the channel rule of the comune, then the declaration of the host). Switzerland: <c>Booking.TouristTax</c> when it
/// has one, else the canton rule of <c>SwissTouristTax:Rules</c> for the guests and the nights; never for a channel the profile says collects it itself. A portal that collects
/// it gives no line: never a double charge to the guest.</item>
/// <item><b>Who collected the money.</b> <see cref="FiscalPaymentChannel.Portal"/> for a booking whose portal collects (the reservation says, else the configured default),
/// <see cref="FiscalPaymentChannel.CasaZen"/> for a booking paid through CasaZen, <see cref="FiscalPaymentChannel.Outside"/> for the others.</item>
/// </list>
/// </remarks>
public interface IGuestDocumentLineBuilder
{
    /// <summary>
    /// The lines of the booking under <paramref name="rule"/>, and who collected the money. <paramref name="platformCollectedTouristTaxChannels"/> are the channels whose portal
    /// collects the tourist tax itself (used for a Swiss document): what <see cref="PlatformCollectedChannelsOf"/> says of the profile of the issuer. 404 when the booking is not
    /// visible to the caller.
    /// </summary>
    Task<GuestDocumentLines> BuildAsync(
        Guid bookingId,
        FiscalTaxRule rule,
        IReadOnlyList<BookingSource> platformCollectedTouristTaxChannels,
        CancellationToken ct = default);

    /// <summary>
    /// The channels whose portal collects the tourist tax of the guests of an issuer (⚖ C5): the ones the issuer confirmed, an empty list included (E11/F01/US01/T04: «no portal
    /// collects it» is an answer, never «use the default»); while it has confirmed none, a Swiss issuer has the shipped default <c>SwissTouristTax:PlatformCollected</c> and an
    /// Italian one has none (its channel rules are the ones of the comune). The default is read here and never copied into the profile, so the step «Tassa di soggiorno» of the
    /// billing setup stays open until the issuer itself confirms the channels.
    /// </summary>
    IReadOnlyList<BookingSource> PlatformCollectedChannelsOf(OrgFiscalIssuerProfile profile);
}

/// <summary>The lines of a guest document as the guest pays them (VAT included), in order, with who collected the money.</summary>
/// <param name="Lines">The lines, each with its type; empty when the booking has nothing to bill (the amounts are then refused).</param>
/// <param name="PaymentChannel">Who collected the money: decides the payment state the document shows.</param>
public sealed record GuestDocumentLines(IReadOnlyList<FiscalDocumentGrossLine> Lines, FiscalPaymentChannel PaymentChannel);
