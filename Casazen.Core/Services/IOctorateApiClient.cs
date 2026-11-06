namespace Casazen.Core.Services;

/// <summary>
/// The Octorate API (ADR-023), against the real Octorate sandbox or production: there is no mock and no fixture adapter (PO decision
/// 2026-11-04, ADR-023 amended on 2026-11-05). One method per Octorate operation; a later task adds the operation it needs, with the
/// same error mapping and the same rate-limit gate. No method takes an org: every call acts for <see cref="IOctorateOrgScope.OrgId"/>,
/// which is how tenant isolation is enforced (ADR-023-R4).
/// </summary>
/// <remarks>
/// Errors are typed (<c>Casazen.Infrastructure.Octorate.OctorateException</c> and its subclasses): not found, conflict, refused
/// credentials or token (also a refusal of the guest data before CasaZen's outbound IPs are whitelisted, never an empty result), wrong
/// environment, rate limit, server failure. A token Octorate refuses (403 <c>ApiLoginInvalid</c>) is refreshed and the call made again
/// once; a 429 is retried after 1, 2 and 4 seconds.
/// </remarks>
public interface IOctorateApiClient
{
    /// <summary>The application's configuration: the environment of the credentials, the quota (<c>GET /rest/v1/api/configuration</c>).</summary>
    Task<OctorateConfiguration> GetConfigurationAsync(CancellationToken ct);

    /// <summary>
    /// The accommodations the connection of the org sees (<c>GET /rest/v1/accommodation</c>): with an OAuth consent the ones of that
    /// consent, an agency's whole network included. Every page is read.
    /// </summary>
    Task<OctorateProperty[]> GetPropertiesAsync(CancellationToken ct);

    /// <summary>One accommodation (<c>GET /rest/v1/accommodation/{acc}</c>).</summary>
    Task<OctorateProperty> GetPropertyAsync(string accommodationId, CancellationToken ct);

    /// <summary>
    /// The police account of one accommodation: the link to Alloggiati Web that the host set on the Octorate panel
    /// (<c>GET /rest/v1/accommodation/{acc}?fields=policeAccount</c>, Q-002, E03/F01/US02/T01). A read of its own, not a member of
    /// <see cref="GetPropertyAsync"/>: the account is asked for by name, as Octorate documents it, and the card read for the import is
    /// left as it was. Only <c>valid</c>, <c>autorun</c> and <c>lastCron</c> are read; the answer also holds the Alloggiati code and the web
    /// service key, which are never read, logged or kept. A member Octorate leaves out, or an accommodation without a police account, is
    /// <c>null</c> in the result and not an error.
    /// </summary>
    Task<OctoratePoliceAccount> GetPoliceAccountAsync(string accommodationId, CancellationToken ct);

    /// <summary>Changes the members of <paramref name="update"/> that are set, and nothing else (<c>PATCH /rest/v2/accommodation/{acc}</c>).</summary>
    Task UpdatePropertyAsync(string accommodationId, OctoratePropertyUpdate update, CancellationToken ct);

    /// <summary>
    /// The products of an accommodation, rooms and the rates derived from them, with their guests, bedrooms, bathrooms and beds
    /// (<c>GET /rest/v3/roomrates/{acc}?fields=...</c>; the v3 read requires <c>fields</c>).
    /// </summary>
    Task<OctorateRoom[]> GetRoomsAsync(string accommodationId, CancellationToken ct);

    /// <summary>
    /// The channel connections of an accommodation and the id of the listing on each channel
    /// (<c>GET /rest/v1/connection?accommodation={acc}&amp;fields=id,channelId,channelName,hotelId,status</c>): only those fields are asked,
    /// so the portal credentials a connection holds never reach CasaZen.
    /// </summary>
    Task<OctorateChannelConnection[]> GetConnectionsAsync(string accommodationId, CancellationToken ct);

    /// <summary>
    /// One page of the reservations of an accommodation (<c>GET /rest/v1/reservation/{acc}</c>, E02/F02/US01/T01): the kind of list,
    /// the Rome dates, the page (from 0) and its size (at most 200). The list has the identification, the dates, the state and
    /// <c>updateTime</c> of each reservation, not the payments nor the price breakdown (<see cref="GetReservationAsync"/> has them). An
    /// item without a usable id is left out of the reservations of the page but counted in its <see cref="OctorateReservationPage.ItemCount"/>.
    /// The list ends with a page that has no item at all, not with a short one: a server may serve fewer reservations than the size asked for.
    /// </summary>
    Task<OctorateReservationPage> GetReservationsAsync(string accommodationId, OctorateReservationQuery query, CancellationToken ct);

    /// <summary>One reservation with its payments and price breakdown (<c>GET /rest/v1/reservation/{acc}/{id}</c>); 404 is <c>OctorateNotFoundException</c>.</summary>
    Task<OctorateReservation> GetReservationAsync(string accommodationId, string reservationId, CancellationToken ct);

    /// <summary>
    /// The calendar of the given products of an accommodation from <paramref name="from"/> to <paramref name="to"/>, both included
    /// (<c>GET /rest/v1/calendar/{acc}</c>, E02/F02/US01/T03; at most 20 products a call, more are asked in several calls). An answer that
    /// holds no recognizable day for a non-empty range is <c>OctorateServerException</c>, never an empty calendar: a caller that deprecates
    /// something after a successful read must not do it on an answer it could not read.
    /// </summary>
    Task<OctorateCalendarDay[]> GetCalendarAsync(
        string accommodationId,
        IReadOnlyCollection<string> productIds,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);

    /// <summary>Cancels a reservation (<c>DELETE /rest/v1/reservation/{acc}/{id}</c>): a change of the status to <c>CANCELLED</c> with <c>PATCH</c> answers 400.</summary>
    Task CancelReservationAsync(string accommodationId, string reservationId, CancellationToken ct);

    /// <summary>The state of the stay CasaZen owns (<c>PUT /rest/v1/checkin/{acc}/{id}?status=</c>): arrival, departure, no-show.</summary>
    Task SetCheckInStatusAsync(string accommodationId, string reservationId, OctorateCheckInStatus status, CancellationToken ct);

    /// <summary>
    /// Octorate's availability check of the room of <paramref name="target"/> for the nights from <paramref name="from"/> to the night before
    /// <paramref name="to"/> (<c>GET /rest/v1/calendar/{acc}/{productId}/availabilityCheck?startDate=&amp;endDate=</c>, E08/F01/US01/T02): 200 is
    /// available, 406 is not, with the reason Octorate gives. The operation takes no occupancy: <paramref name="guests"/> is checked (at least
    /// one) and the capacity stays CasaZen's own check. Any other answer is the typed error of the client, never "available".
    /// </summary>
    Task<OctorateAvailabilityResult> CheckAvailabilityAsync(
        OctorateBookingTarget target,
        DateOnly from,
        DateOnly to,
        int guests,
        CancellationToken ct);

    /// <summary>
    /// Creates the reservation of a CasaZen booking (<c>POST /rest/v1/reservation/{acc}?availability=true</c>, E08/F01/US01/T02): the stay as the
    /// UTC instants its Rome days start, the guests, <c>roomGross</c>, the direct channel of <c>Octorate:DirectChannelId</c>, the booking code as
    /// <c>refer</c>, the room of the target (<c>product</c>, and <c>pmsProduct</c> when the target has one), and <c>availability=true</c> so the
    /// night is taken off every channel. Never a field of Octorate's automatic charge (D9). Octorate has no idempotency key (D13): after a
    /// timeout, a 429 or a 5xx the caller looks the reservation up with <see cref="FindReservationByExternalReferenceAsync"/> before any retry.
    /// An answer without a reservation id is <c>OctorateServerException</c>: the reservation is then found by its reference, never assumed.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>Octorate:DirectChannelId</c> is not a channel id: nothing leaves.</exception>
    Task<OctorateReservation> CreateReservationAsync(OctorateBookingTarget target, OctorateReservationCreate request, CancellationToken ct);

    /// <summary>
    /// Records a payment on a reservation (<c>POST /rest/v1/reservation/{acc}/{id}/payment</c>, E08/F01/US01/T02): income CasaZen collected,
    /// "not a card charge" (D9): <c>transaction</c>, <c>paymentMode</c>, <c>amount</c> to the cent, <c>referenceTime</c>. Never a card, a
    /// pre-authorization or a scheduled charge.
    /// </summary>
    Task<OctoratePaymentResult> RegisterPaymentAsync(
        string accommodationId,
        string reservationId,
        OctoratePaymentRegistration payment,
        CancellationToken ct);

    /// <summary>
    /// The reservation CasaZen created with <paramref name="externalReference"/> as its <c>refer</c> (the booking code), found in the list of the
    /// reservations created in the last <c>30</c> days (<c>type=CREATETIME</c>, 200 a page, page after page): how a create whose answer was lost
    /// is recovered, Octorate having no idempotency key (D13). <c>null</c> only when the whole list was read and has none; a list that could not
    /// be read whole (an item that could not be read, too many pages) is <c>OctorateServerException</c>, so a caller never creates a second
    /// reservation on a guess.
    /// </summary>
    Task<OctorateReservation?> FindReservationByExternalReferenceAsync(string accommodationId, string externalReference, CancellationToken ct);

    /// <summary>
    /// The PMS rooms of an accommodation (<c>GET /rest/v1/pms/{acc}</c>): each with the unit it belongs to (<c>parentId</c>). The id Octorate
    /// wants as <c>pmsProduct</c> to create and change a reservation on a unit that has one (sandbox 2026-11-04).
    /// </summary>
    Task<OctoratePmsRoom[]> GetPmsRoomsAsync(string accommodationId, CancellationToken ct);

    /// <summary>
    /// The catalog of portals and tools Octorate offers (<c>GET /rest/v1/meta/portals</c>, E09/F01/US01/T02). A partner-level call: it acts
    /// for the application and carries no property data, so it needs no org in <see cref="IOctorateOrgScope"/>, uses the partner token
    /// (<see cref="IOctorateTokenService.GetPartnerAccessTokenAsync"/>) and takes its call from the rate-limit bucket <c>platform</c>, not an
    /// org's. Same HTTPS client, timeout, typed errors and backoff as the other calls. An item with no usable identifier is left out; there
    /// is one method and one <see cref="OctoratePortal"/> for every reader of the catalog (the marketplace cache, the channel costs report).
    /// </summary>
    Task<OctoratePortal[]> GetPortalsAsync(CancellationToken ct);

    /// <summary>
    /// Every connection of a channel the org's token sees, in one call (<c>GET /rest/v1/connection?fields=id,channelId,hotelId,status,
    /// userLogged,loginAt,accommodationId</c>, E02/F01/US03): how the import finds, after the person gave the access in the Octorate panel, the
    /// connection of each listing to a channel, whether it was authorized and when the person logged in. Only those fields are asked, so the
    /// portal credentials a connection holds never reach CasaZen. An item without a numeric id, a channel or an accommodation is left out.
    /// </summary>
    Task<OctorateConnectionSummary[]> GetOrgConnectionsAsync(CancellationToken ct);

    /// <summary>
    /// <c>IMPORT_ROOMS</c> on one connection (<c>POST /rest/v1/connection/IMPORT_ROOMS/{acc}/{connection}</c>, E02/F01/US03, D6): Octorate
    /// reads the rooms of the existing listing on the channel, ready to be mapped. It creates and publishes nothing on the channel. An
    /// answer that cannot be read as the list of mappings the operation documents is <c>OctorateServerException</c>, never a success.
    /// </summary>
    Task<OctorateConnectionActionResult> ImportRoomsAsync(string accommodationId, string connectionId, CancellationToken ct);

    /// <summary><c>IMPORT_RESERVATIONS</c> on one connection (the future reservations of the channel, inside Octorate): same shape and errors as <see cref="ImportRoomsAsync"/>.</summary>
    Task<OctorateConnectionActionResult> ImportReservationsAsync(string accommodationId, string connectionId, CancellationToken ct);

    /// <summary><c>IMPORT_CALENDAR</c> on one connection (the calendar of the channel, inside Octorate): same shape and errors as <see cref="ImportRoomsAsync"/>.</summary>
    Task<OctorateConnectionActionResult> ImportCalendarAsync(string accommodationId, string connectionId, CancellationToken ct);

    /// <summary>
    /// <c>MARK_CONNECTED</c> on one connection (tells Octorate, and the channel when needed, that the connection is ready): same shape and
    /// errors as <see cref="ImportRoomsAsync"/>. Called only for a listing a person confirmed.
    /// </summary>
    Task<OctorateConnectionActionResult> MarkConnectedAsync(string accommodationId, string connectionId, CancellationToken ct);
}
