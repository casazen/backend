using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Where in Octorate a CasaZen property books (E08/F01/US01/T02): the accommodation of its confirmed mapping, the room (unit, the
/// <c>product</c>) of that mapping, and the PMS room of the unit when it has exactly one. Given only by <see cref="IOctorateBookingTargets"/>:
/// every writer of a booking goes through it, so nothing is written for a property whose mapping is not confirmed.
/// </summary>
/// <param name="AccommodationId">The accommodation id (<c>OctorateListingMapping.OctorateAccommodationId</c>).</param>
/// <param name="ProductId">The room (unit) the property books into (<c>OctorateListingMapping.OctorateProductId</c>).</param>
/// <param name="PmsProductId">
/// The PMS room of the unit (<c>GET /rest/v1/pms/{acc}</c>, <c>parentId</c> = the unit), which Octorate wants as <c>pmsProduct</c> to create
/// and change a reservation on a unit that has one (sandbox 2026-11-04: any other <c>pmsProduct</c> answers 500); <c>null</c> when the unit has
/// none, or several (nothing is guessed), and the reservation is then created on the unit alone.
/// </param>
public sealed record OctorateBookingTarget(string AccommodationId, string ProductId, string? PmsProductId = null);

/// <summary>
/// What Octorate's availability check of a room answered (<c>GET /rest/v1/calendar/{acc}/{productId}/availabilityCheck</c>): 200 "available
/// to book", 406 "not available to book" with the reason Octorate gives (<c>Not sellable</c>, <c>Day size mismatch</c>, ...).
/// </summary>
/// <param name="Available">True only when Octorate answered 200.</param>
/// <param name="Reason">Octorate's short reason of a refusal (at most 200 characters, never a body); <c>null</c> when available.</param>
public sealed record OctorateAvailabilityResult(bool Available, string? Reason);

/// <summary>
/// A reservation CasaZen creates on Octorate for one of its own bookings (<c>POST /rest/v1/reservation/{acc}</c>, E08/F01/US01/T02, D9, D13).
/// The client adds the direct channel of configuration (<c>Octorate:DirectChannelId</c>), the creation and update instants and
/// <c>availability=true</c>; it never adds a field of Octorate's automatic charge (<c>scheduled*</c>, <c>preauth*</c>, <c>stopCron</c>).
/// </summary>
public sealed record OctorateReservationCreate
{
    /// <summary>The CasaZen booking code, sent as <c>refer</c>: how the reservation is found again when the answer of a create was lost.</summary>
    public required string ExternalReference { get; init; }

    /// <summary>The arrival day of the stay (Europe/Rome); sent as the UTC instant that day starts in Rome.</summary>
    public required DateOnly CheckIn { get; init; }

    /// <summary>The departure day of the stay (Europe/Rome), not a night; sent as the UTC instant that day starts in Rome.</summary>
    public required DateOnly CheckOut { get; init; }

    /// <summary>All the guests (<c>totalGuest</c>), at least one.</summary>
    public required int Guests { get; init; }

    /// <summary>The minors among <see cref="Guests"/> (<c>totalChildren</c>).</summary>
    public required int Children { get; init; }

    /// <summary>
    /// The price of the stay without the tourist tax, to the cent (<c>roomGross</c>): <see cref="Booking.BasePrice"/>, lodging and cleaning,
    /// the same figure the import reads back from <c>roomGross</c>.
    /// </summary>
    public required decimal RoomGross { get; init; }

    /// <summary>The first name of the guest who booked (<c>guests[0].givenName</c>, type <c>BOOKER</c>).</summary>
    public required string BookerFirstName { get; init; }

    /// <summary>The last name of the guest who booked (<c>guests[0].familyName</c>).</summary>
    public required string BookerLastName { get; init; }

    /// <summary>
    /// The sex of the booker when CasaZen knows it (<c>MALE</c>, <c>FEMALE</c>); <c>null</c> otherwise, and then it is not sent: it is never
    /// made up. No e-mail and no phone are ever sent.
    /// </summary>
    public Gender? BookerGender { get; init; }
}

/// <summary>
/// A payment of a booking recorded on its Octorate reservation (<c>POST /rest/v1/reservation/{acc}/{id}/payment</c>): income CasaZen
/// collected, "not a card charge" (D9). Only <c>transaction</c>, <c>paymentMode</c>, <c>amount</c>, <c>referenceTime</c> and
/// <c>description</c> are sent: never a card, a pre-authorization or a scheduled charge.
/// </summary>
/// <param name="Transaction">The external id: the Stripe PaymentIntent id, or <c>casazen-{paymentId}</c> for a payment the host recorded.</param>
/// <param name="PaymentMode">Octorate's <c>paymentMode</c> (<c>CASH</c>, <c>BANKTRANSFER</c>, ...), from the configured mapping.</param>
/// <param name="Amount">The amount, to the cent.</param>
/// <param name="ReceivedAt">When the money came in (UTC), sent as <c>referenceTime</c>.</param>
public sealed record OctoratePaymentRegistration(string Transaction, string PaymentMode, decimal Amount, DateTime ReceivedAt);

/// <summary>What Octorate answered to a payment registration: the id of the payment on the reservation, when it gave one.</summary>
public sealed record OctoratePaymentResult(string? PaymentId);

/// <summary>A PMS room of an accommodation (<c>GET /rest/v1/pms/{acc}</c>): its id and the unit (<c>parentId</c>) it belongs to.</summary>
public sealed record OctoratePmsRoom(string Id, string? ParentId, string? Name);
