namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Event types that can trigger automation routines (NF-26, ADR-022).
/// Phase 1 events as specified in AC-NF26-2.
/// </summary>
public enum RoutineEventType
{
    // Booking lifecycle events
    BookingNew,
    BookingConfirmed,
    BookingModified,
    BookingCanceled,

    // Check-in/check-out timing events (relative to dates)
    BeforeCheckin,
    AfterCheckin,
    BeforeCheckout,
    AfterCheckout,

    // Check-in/check-out occurrence events
    CheckinOccurred,
    CheckoutOccurred,

    // Guest flow completion events
    OnlineCheckinCompleted,
    IdVerificationCompleted,

    // Compliance events
    AlloggiatiReceiptArrived,

    // Communication events
    GuestMessageReceived,
    ReviewReceived,

    // Payment events
    PaymentSucceeded,
    PaymentFailed,

    // Activity events
    ActivityCompleted,

    // Recurring events
    WeeklyRecurrence
}
