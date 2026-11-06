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

    /// <summary>
    /// The state of the police account of one accommodation, as the guided step of the police connection needs it (E10/F01/US03/T01, D17,
    /// D31): the same read as <see cref="GetPoliceAccountAsync"/> (same error mapping and rate-limit gate), answered as
    /// <see cref="OctoratePoliceAccountStatus"/>. It never throws for what only means «CasaZen cannot tell»: the field absent (an accommodation
    /// created through the API has none: S25), a read Octorate refused (credentials, permission, environment) and an accommodation Octorate does
    /// not know are <see cref="OctoratePoliceAccountStatus.Readable"/> false. A failure worth a later retry (429, 5xx, no answer) is the typed
    /// error of the client, so a caller never takes a failed read for a verdict.
    /// </summary>
    Task<OctoratePoliceAccountStatus> GetPoliceAccountStatusAsync(string octorateAccommodationId, CancellationToken ct);

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
    /// The e-mail address and the phone number of one guest of the guest registry of an accommodation (<c>POST /rest/v1/guests/{acc}/{guestId}/contactInfo</c>,
    /// E07/F06/US02/T02, S24). Sensitive data: Octorate gives it only to an application with the dedicated permission and, even in the sandbox, only from the
    /// static IP addresses registered with it (S7, S18, D24), and logs every read. So it is asked for one guest of one booking at a time, never in bulk
    /// (<see cref="IOctorateContactsService"/>), and nothing of the answer is ever logged. A 401 or a 403 (no static IP, no permission) is
    /// <c>OctorateSensitiveDataNotAllowedException</c>, never an empty result: a caller must not read "no contact" into a refusal.
    /// </summary>
    Task<OctorateGuestContactInfo> GetGuestContactInfoAsync(string accommodationId, string guestId, CancellationToken ct);

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

    /// <summary>
    /// Cancels the reservation of a booking the way Octorate documents it (E08/F02/US02/T02, NF-76): the reservation is read, and when it is
    /// <c>CANCELLED</c> already nothing is sent (the retry of a cancellation whose answer was lost is done); otherwise the same document goes back
    /// with <c>PUT /rest/v1/reservation/{acc}/{id}</c> and only its <c>status</c> changed to <c>CANCELLED</c> (<c>PATCH</c> cannot move a confirmed
    /// reservation to cancelled, and a PUT with less than the whole reservation could rewrite what it leaves out). A 2xx of the PUT is not taken for
    /// the proof (Octorate ignores, without saying so, what it does not write): the reservation is read again and only a <c>CANCELLED</c> reservation
    /// is a success. When Octorate answers that the reservation cannot be cancelled that way (a refusal of the request itself,
    /// <c>OctorateRequestRejectedException</c>), or accepts the PUT and the reservation is still not cancelled, it is <c>DELETE</c>d instead, the way
    /// the sandbox proved on 2026-11-04. Any other answer, the read that checks the PUT included, is the typed error of the client, never a success:
    /// not found, conflict, refused credentials, rate limit, server failure. <paramref name="target"/> names the accommodation and gives the room
    /// (<c>product</c>) and PMS room (<c>pmsProduct</c>) the document is completed with when the answer left them out.
    /// </summary>
    Task CancelReservationAsync(OctorateBookingTarget target, string reservationId, CancellationToken ct);

    /// <summary>The state of the stay CasaZen owns (<c>PUT /rest/v1/checkin/{acc}/{id}?status=</c>): arrival, departure, no-show.</summary>
    Task SetCheckInStatusAsync(string accommodationId, string reservationId, OctorateCheckInStatus status, CancellationToken ct);

    /// <summary>
    /// Creates or updates the check-in guests of a reservation (E03/F01/US01/T03, NF-41), one call per guest: a guest with no
    /// <see cref="OctorateCheckInGuestUpsert.OctorateGuestId"/> is created (<c>POST /rest/v1/checkin/{acc}/{id}/guest</c>), one with an id is
    /// updated (<c>PUT /rest/v1/checkin/{acc}/{id}/guest/{guestId}</c>). The result says, per guest, the Octorate id and whether it is
    /// validated, or a stable error code: a refusal of one guest does not stop the others, and no error is turned into a success.
    /// </summary>
    Task<OctorateCheckInGuestResult[]> UpsertCheckInGuestsAsync(
        string accommodationId,
        string reservationId,
        IReadOnlyList<OctorateCheckInGuestUpsert> guests,
        CancellationToken ct);

    /// <summary>Deletes a check-in guest of a reservation (<c>DELETE /rest/v1/checkin/{acc}/{id}/guest/{guestId}</c>).</summary>
    Task DeleteCheckInGuestAsync(string accommodationId, string reservationId, string octorateGuestId, CancellationToken ct);

    /// <summary>The arrival Octorate registered for a reservation (<c>effectiveCheckin</c>, <c>arrivalTime</c> of the single read).</summary>
    Task<OctorateReservationArrival> GetReservationArrivalAsync(string accommodationId, string reservationId, CancellationToken ct);

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
    /// One page of the chat threads of an accommodation (<c>GET /rest/v1/chat/{property}/threads?offset=&amp;filterReadStatus=false</c>,
    /// E05/F01/US01/T02): the threads of the OTA conversations, read and unread. The list has no page size: <paramref name="offset"/> is the number
    /// of threads already read, and the list ends with a page that has no item at all. The chat API is per accommodation, so there is no coarse
    /// "messages since" read: this one, <see cref="GetChatThreadAsync"/>, <see cref="GetChatMessagesAsync"/> and
    /// <see cref="GetRecentChatMessagesAsync"/> are the reads of the copy; the send operation is E05/F02's. An answer that is not a list is
    /// <c>OctorateServerException</c>, never an empty page. A 403 that is not an error of Octorate's API, which is what a license without the
    /// <c>chat</c> module gets, is <c>OctorateAuthException</c> with the code <c>chat_not_licensed</c> (decided by the code and the shape of the
    /// answer, never its text), the same for the thread and its messages; <see cref="GetRecentChatMessagesAsync"/> answers 500 instead, see there.
    /// </summary>
    Task<OctorateChatThreadPage> GetChatThreadsAsync(string accommodationId, int offset, CancellationToken ct);

    /// <summary>
    /// One chat thread (<c>GET /rest/v1/chat/{property}/threads/{id}</c>, E05/F01/US01/T02): its default processor, the reservation it is linked to,
    /// the connection it comes through and the flag of Octorate's own assistant for the thread (<c>aiDriven</c>). 404 is <c>OctorateNotFoundException</c>.
    /// </summary>
    Task<OctorateChatThread> GetChatThreadAsync(string accommodationId, string threadId, CancellationToken ct);

    /// <summary>
    /// The messages of a thread, all pages (<c>GET /rest/v1/chat/{property}/threads/{thread}/messages?createdAfter=</c>, E05/F01/US01/T02): the ones
    /// created after <paramref name="createdAfter"/>, or all of them when it is <c>null</c>. A thread that says it has more pages and does not give them
    /// is <c>OctorateServerException</c>, never a short thread read as a whole one.
    /// </summary>
    Task<OctorateChatMessage[]> GetChatMessagesAsync(string accommodationId, string threadId, DateTime? createdAfter, CancellationToken ct);

    /// <summary>
    /// The messages created in the last 24 hours in an accommodation (<c>GET /rest/v1/accommodation/recentMessages?property=</c>, E05/F01/US01/T02):
    /// how the reconciliation finds the threads that had news, whatever the webhook missed. The operation is per accommodation in the OpenAPI
    /// (the sandbox call carries <c>property</c>), so it takes the accommodation. Without the <c>chat</c> module this operation does not answer 403
    /// like the others: the sandbox answers it with a 500 that is not an error of Octorate's API (no <c>type</c>), and that is "chat not available" as
    /// well (<c>sandbox-run-results.md</c>): <c>OctorateAuthException</c> with the code <c>chat_not_licensed</c>; a 500 that carries a <c>type</c> is
    /// <c>OctorateServerException</c>.
    /// </summary>
    Task<OctorateChatMessage[]> GetRecentChatMessagesAsync(string accommodationId, CancellationToken ct);

    /// <summary>
    /// Sends a message to the guest of a conversation of a channel (<c>POST /rest/v1/chat/{property}/messages</c>, E05/F02/US02/T01): processor
    /// <c>CHANNEL</c>, the words as the attribute <c>TEXT</c> and <c>OTA_FLOW_DIRECTION</c> = <c>TO</c>, written as the person of the host side that
    /// the thread names (<see cref="OctorateChatThread.HostPersonId"/>) on the thread the channel gave (<see cref="OctorateChatThread.ExternalThreadId"/>),
    /// both read from the thread first; a thread that names none of them is <c>OctorateChatNotSendableException</c> and nothing is sent. Answers the id
    /// Octorate gave the message and the state it reports; a 2xx answer without an id is <c>OctorateServerException</c>, because the message may exist
    /// all the same. <b>The send is never repeated by the client</b> (OCT-13): a timeout, a 5xx or an answer that cannot be read leaves the outcome
    /// unknown, and only the caller, after the copy of the conversation (<see cref="IMessageSyncService"/>) has shown whether the message arrived,
    /// decides about a second send. The words are personal data and are never logged. A license without the <c>chat</c> module is
    /// <c>OctorateAuthException</c> with the code <c>chat_not_licensed</c>, as for the reads. <b>Documented, not proven:</b> the sandbox has no chat
    /// license, so the body follows the OpenAPI (<c>ChatMessageDTO</c>) and the examples of the collection of Octorate.
    /// </summary>
    Task<OctorateChatSendResult> SendChatMessageAsync(string accommodationId, string threadId, string text, CancellationToken ct);

    /// <summary>
    /// The e-mail address and the phone number of the booker of ONE reservation (<c>POST /rest/v2/reservation/{accommodation}/{refer}/contactInfo</c>,
    /// E05/F02/US02/T01, S7, D24); <paramref name="refer"/> is the <c>refer</c> of the reservation (<see cref="OctorateReservation.Refer"/>). Sensitive
    /// data: Octorate gives it only to an application with the dedicated permission and, even in the sandbox, only from the static IP addresses
    /// registered with it, and records every read in its own log, so it is asked for one booking at a time, when a person needs it, never in bulk, and
    /// nothing of the answer is ever logged. A 401 or a 403 (no static IP, no permission) is <c>OctorateSensitiveDataNotAllowedException</c>, never an
    /// empty result: a caller must not read "no contact" into a refusal. The answer has the same shape as <see cref="GetGuestContactInfoAsync"/>
    /// (<c>email</c>, <c>phone</c>), and <see cref="OctorateGuestContactInfo.Recognized"/> says whether it was understood.
    /// </summary>
    Task<OctorateGuestContactInfo> GetReservationContactInfoAsync(string accommodationId, string refer, CancellationToken ct);

    /// <summary>
    /// The message templates the channel manager keeps for a thread (<c>GET /rest/v1/chat/{property}/threads/{thread}/templates</c>, E05/F02/US02/T02,
    /// <c>MailTemplateDTO</c>): read only, to be copied into the composer. An item without a usable id or a body is left out; an answer that is not a
    /// list is <c>OctorateServerException</c>, never an empty list. The same refusals as the other chat reads (<c>chat_not_licensed</c>).
    /// </summary>
    Task<OctorateChatTemplate[]> GetChatTemplatesAsync(string accommodationId, string threadId, CancellationToken ct);

    /// <summary>
    /// The text of a message of a thread in <paramref name="language"/> (<c>PATCH /rest/v1/chat/{property}/threads/{threadId}/messages/{messageId}/translate/{language}</c>,
    /// E05/F02/US02/T02), translated by the channel manager: the fallback of CasaZen's own translation. Octorate keeps the translation on its message
    /// (the attribute <c>TRANSLATION_MAP</c>); CasaZen reads it from the answer and stores nothing. A message is translated by its id, never a text of
    /// CasaZen's, so no words leave CasaZen. An answer that holds no translation in that language is <c>OctorateServerException</c>.
    /// <b>Documented, not proven:</b> the wire shape of the translation map is not seen in the sandbox (no chat license). <paramref name="language"/> is
    /// one of the 14 languages of the channel manager (<c>IT</c>, <c>EN</c>, ...).
    /// </summary>
    Task<string> TranslateChatMessageAsync(string accommodationId, string threadId, string messageId, string language, CancellationToken ct);

    /// <summary>
    /// The catalog of portals and tools Octorate offers (<c>GET /rest/v1/meta/portals</c>, E09/F01/US01/T02). A partner-level call: it acts
    /// for the application and carries no property data, so it needs no org in <see cref="IOctorateOrgScope"/>, uses the partner token
    /// (<see cref="IOctorateTokenService.GetPartnerAccessTokenAsync"/>) and takes its call from the rate-limit bucket <c>platform</c>, not an
    /// org's. Same HTTPS client, timeout, typed errors and backoff as the other calls. An item with no usable identifier is left out; there
    /// is one method and one <see cref="OctoratePortal"/> for every reader of the catalog (the marketplace cache, the channel costs report).
    /// </summary>
    Task<OctoratePortal[]> GetPortalsAsync(CancellationToken ct);

    /// <summary>
    /// The billing data of the booker of a reservation, read from the customer record (the address book of the guests) the reservation names with its
    /// <c>invoiceHolderId</c> (<see cref="GetReservationAsync"/>, then <c>GET /rest/v1/guests/{acc}/{guestId}</c>, the <c>GuestDTO</c> of the OpenAPI,
    /// E06/F02/US01/T04). <c>null</c> when the reservation names no customer record (most do not) or the record holds no billing data (a guest's name
    /// alone is not billing data): no error, and nothing is guessed. <b>Names from the OpenAPI, wire shape not proven:</b> the operations declare no
    /// answer and no call could be made to the sandbox (<c>OctorateBillingMapping</c>), which also needs CasaZen's outbound IPs whitelisted for the
    /// guests' sensitive data (S7, D24). That refusal (403) is <c>OctorateAuthException</c>, never an empty result; a reservation or a record Octorate does
    /// not know is <c>OctorateNotFoundException</c>.
    /// </summary>
    Task<OctorateBillingData?> GetBookerBillingDataAsync(string accommodationId, string reservationId, CancellationToken ct);

    /// <summary>
    /// Writes the billing data to the customer record the reservation names with its <c>invoiceHolderId</c> (<c>PUT /rest/v1/guests/{acc}/{guestId}</c>,
    /// E06/F02/US01/T04): only the billing members of <paramref name="data"/> that are set (<c>OctorateBillingMapping.WrittenMembers</c>), never an
    /// identity document, an Alloggiati datum, a phone number or the e-mail address of the guest, and nothing is cleared. <c>true</c> once Octorate
    /// answered 2xx; <c>false</c> when the reservation names no customer record: nothing was sent, no record is made, and the caller must not take the
    /// data for written. Documented, not proven, as <see cref="GetBookerBillingDataAsync"/>: whether the <c>PUT</c> merges or replaces the record, and
    /// whether Octorate keeps every member it is given, are the first things to check in the sandbox. Data with no member to write is refused
    /// (<see cref="ArgumentException"/>).
    /// </summary>
    Task<bool> UpsertBookerBillingDataAsync(string accommodationId, string reservationId, OctorateBillingData data, CancellationToken ct);

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

    /// <summary>
    /// Creates an accommodation for the application (<c>POST /rest/v2/accommodation</c>, E02/F03/US01/T06, PO-19): a partner-level call, made with
    /// the partner token (<see cref="IOctorateTokenService.GetPartnerAccessTokenAsync"/>) because the accommodation does not exist yet. The answer
    /// carries the id of the accommodation and its refresh token, <b>once</b>: the caller saves the token before anything else
    /// (<see cref="IOctorateTokenService.SaveTokensAsync"/>). An answer without the id or without the token is <c>OctorateServerException</c>, never a
    /// success. A 400 <c>ApiParamsExemption</c> is <c>OctoratePropertyRejectedException</c> with the fields; a 412 on the network is
    /// <c>OctorateNetworkUnknownException</c>. Octorate has no idempotency key: a call whose answer was lost is never repeated by the client.
    /// </summary>
    Task<OctoratePropertyCreated> CreatePropertyAsync(OctoratePropertyCreate request, CancellationToken ct);

    /// <summary>
    /// Creates the unit of a listing (<c>POST /rest/v3/roomrates/{acc}</c>, E02/F03/US01/T02) and returns its product id. An answer without a
    /// product id is <c>OctorateServerException</c>: the unit is then looked up by its name, never assumed.
    /// </summary>
    Task<string> CreateRoomAsync(string accommodationId, OctorateRoomCreate room, CancellationToken ct);

    /// <summary>Writes the descriptions of a unit (<c>POST /rest/v1/content/products/{acc}/{pid}/descriptions</c>); a member that is empty is not sent.</summary>
    Task UpdateDescriptionsAsync(string accommodationId, string productId, OctorateDescriptions descriptions, CancellationToken ct);

    /// <summary>
    /// The descriptions of a unit as the channel manager holds them (<c>GET .../descriptions</c>); <c>null</c> while it holds none (404
    /// <c>ApiResourceEmpty</c>). Read only: the baseline of the changes made outside CasaZen and the adoption of a text.
    /// </summary>
    Task<OctorateDescriptions?> GetDescriptionsAsync(string accommodationId, string productId, CancellationToken ct);

    /// <summary>Writes the rooms of a unit with their beds (<c>POST .../rooms</c>, one call for each room).</summary>
    Task UpdateRoomsAsync(string accommodationId, string productId, IReadOnlyList<OctorateContentRoom> rooms, CancellationToken ct);

    /// <summary>The rooms of a unit as the channel manager holds them (<c>GET .../rooms</c>); empty while it holds none.</summary>
    Task<IReadOnlyList<OctorateContentRoom>> GetContentRoomsAsync(string accommodationId, string productId, CancellationToken ct);

    /// <summary>
    /// Writes the amenities of a unit (<c>POST .../amenities</c>) from the ids of the catalog of the channel manager. Called only when
    /// <c>ListingChannels:SendAmenities</c> is on: the codes of <c>AmenityCatalogMapper</c> were not compared with the sandbox yet.
    /// </summary>
    Task UpdateAmenitiesAsync(string accommodationId, string productId, IReadOnlyCollection<long> amenityIds, CancellationToken ct);

    /// <summary>Writes the structured rules of the stay of a unit (<c>POST .../reservation</c>); a member that is not set is not sent.</summary>
    Task UpdateReservationRulesAsync(string accommodationId, string productId, OctorateReservationRules rules, CancellationToken ct);

    /// <summary>
    /// Writes the cancellation policy of a unit (<c>POST .../cancellation</c>). Called only when <c>ListingChannels:SendCancellationPolicy</c> is
    /// on: the members of this part were not proven on the sandbox.
    /// </summary>
    Task UpdateCancellationPolicyAsync(string accommodationId, string productId, OctorateCancellationPolicy policy, CancellationToken ct);

    /// <summary>
    /// Pushes the content of a unit to one channel: <c>POST .../pushQueue/{portal}</c> for the portals that have a queue, otherwise
    /// <c>POST /rest/v1/content/connections/{connection}/push</c>. The answer names the process whose result says whether the channel took it.
    /// </summary>
    Task<OctorateChannelPush> PushChannelAsync(string accommodationId, string productId, OctorateChannelTarget target, CancellationToken ct);

    /// <summary>
    /// Asks the channel manager to download a photo from its public address (<c>POST /rest/v1/accommodation/{acc}/photos</c>, a form body with
    /// <c>url</c>, 202): the answer is the entry of the photo queue.
    /// </summary>
    Task<OctoratePhotoQueueEntry> UploadPhotoAsync(string accommodationId, string photoUrl, CancellationToken ct);

    /// <summary>Removes a photo from the channel manager (<c>DELETE /rest/v1/accommodation/{acc}/photos/{name}</c>); a photo it does not have is not an error.</summary>
    Task DeletePhotoAsync(string accommodationId, string photoName, CancellationToken ct);

    /// <summary>An entry of the photo queue (<c>GET /rest/v1/accommodation/{acc}/queue/{id}</c>); <c>null</c> when the channel manager does not know it.</summary>
    Task<OctoratePhotoQueueEntry?> GetPhotoQueueAsync(string accommodationId, string queueId, CancellationToken ct);

    /// <summary>
    /// The result of a publication process (<c>GET /rest/v1/content/products/{acc}/process/{processId}</c>, OpenAPI <c>readContentProcess</c>): the
    /// path has no product, as the requirements v2.2 (OCT-05) and the OpenAPI say. An answer CasaZen does not understand is
    /// <see cref="OctorateProcessState.Unknown"/>, never a success.
    /// </summary>
    Task<OctorateContentProcess> GetContentProcessAsync(string accommodationId, string processId, CancellationToken ct);
}
