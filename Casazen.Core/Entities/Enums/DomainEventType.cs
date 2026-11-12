namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kinds of domain event written to the transactional outbox (<see cref="OutboxEvent"/>, ADR-022-R2) and published on the
/// event bus. The column stores the number, so every value has an explicit one: a later task appends a value with the next
/// number, an existing number never changes and is never reused.
/// </summary>
/// <remarks>
/// Not to be confused with <see cref="RoutineEventType"/>, the older enum of the routine engine: the routine handler maps these
/// events onto it (E01/F01/US01/T05).
/// </remarks>
public enum DomainEventType
{
    BookingCreated = 1,
    BookingConfirmed = 2,
    BookingModified = 3,
    BookingCancelled = 4,
    CheckInStarted = 5,
    CheckInCompleted = 6,
    CheckOutCompleted = 7,
    IdVerificationCompleted = 8,
    AlloggiatiReceiptArrived = 9,
    ServiceRequestCreated = 10,
    ServiceRequestCompleted = 11,
    PaymentSucceeded = 12,
    PaymentFailed = 13,
    GuestMessageReceived = 14,
    ReviewReceived = 15,

    /// <summary>A scheduled trigger of a stay ("2 days before check-in"), written by <c>TimeEventEmitterJob</c> (ADR-022-R8).</summary>
    TimeTrigger = 16,

    OctorateWebhookReceived = 17,
    OctorateBookingStateChanged = 18,

    /// <summary>
    /// The host declared the property ready after the check-out of a stay, at the check-out wizard or later (E08/F06/US01). About the
    /// booking (<c>EntityId</c>); it closes the cleaning "To do" of that stay. Starts no routine.
    /// </summary>
    PropertyReadyDeclared = 19,

    /// <summary>
    /// The host asked again for the push of a booking to the channel manager (E08/F01/US01/T03,
    /// <c>POST /api/bookings/{id}/octorate-sync/retry</c>): about the booking (<c>EntityId</c>), with the payload of a booking event (origin
    /// System). Runs the push of the booking again and nothing else: no routine and no other consumer of <c>BookingConfirmed</c> (a welcome
    /// message, an alert) runs a second time.
    /// </summary>
    OctorateSyncRequested = 20,

    /// <summary>
    /// The invoice data of a booking was saved (E06/F02/US01/T02, FIS-01): by the host, by the guest at the check-in or by the import of the channel
    /// manager. About the booking (<c>EntityId</c>), with the payload <c>InvoiceDataSavedPayload</c> (the booking and the source, nothing personal). The
    /// consumer that writes the data to Octorate (E06/F02/US01/T04) reads the data from the booking, in its own org. Starts no routine.
    /// </summary>
    InvoiceDataSaved = 21,

    /// <summary>
    /// A property was accepted by the channel manager for the first time (E02/F03/US01/T02): its structure and content are on Octorate. About the
    /// property (<c>EntityId</c>), recorded once; E08/F05/US02/T02 consumes it to send the calendar, the prices and the restrictions. The number
    /// leaves room for the values other tasks of the same wave append after 20.
    /// </summary>
    ListingPublished = 30,

    /// <summary>
    /// A thread of an OTA conversation was read from the channel manager (E05/F01/US01/T02): about the thread (<c>EntityId</c>), with the payload
    /// <c>OtaThreadSyncedPayload</c> (ids and the flag of Octorate's own assistant for the thread). Starts no routine; the guard of E05/F05
    /// consumes it to keep that assistant off (OCT-11). The numbers 50 to 59 are the messages hub (epic E05): the tasks of the waves run in parallel
    /// and each picked a number of its own, so that two of them never pick the same one.
    /// </summary>
    OtaThreadSynced = 50,

    /// <summary>
    /// CasaZen issued a fiscal document of a booking (E06/F01/US01/T01, NF-83): about the document (<c>EntityId</c>), with the payload
    /// <c>FiscalDocumentIssuedPayload</c> (ids and the kind, nothing personal), recorded in the unit of work of the issue. Starts no routine; nothing
    /// consumes it yet. The numbers 60 to 69 are the fiscal documents (epic E06), so the tasks of the waves that run in parallel never pick the same one.
    /// </summary>
    FiscalDocumentIssued = 60,

    /// <summary>CasaZen issued a credit note of one of its documents (E06/F01/US01/T01): about the credit note, with the same payload and the credited document.</summary>
    FiscalDocumentCreditNoteIssued = 61,

    /// <summary>
    /// An org administrator activated a flow on a property after the server verified its prerequisites (E10/F01/US01/T04, NF-104, NF-106):
    /// about the property (<c>EntityId</c>), with the payload <c>OnboardingFlowEventPayload</c> (the property, the flow, the activation row). Recorded
    /// in the same unit of work as the activation row. Starts no routine; nothing consumes it yet. The numbers 100 to 109 are the activation of
    /// CasaZen with Octorate (epic E10), so that the tasks of the waves that run in parallel never pick the same number.
    /// </summary>
    OnboardingFlowActivated = 100,

    /// <summary>An org administrator deactivated a flow on a property (E10/F01/US01/T04): about the property, with the same payload.</summary>
    OnboardingFlowDeactivated = 101,
}
