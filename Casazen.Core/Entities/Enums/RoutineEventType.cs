namespace Casazen.Core.Entities.Enums;

public enum RoutineEventType
{
    BookingNew,
    BookingConfirmed,
    BookingModified,
    BookingCanceled,

    BeforeCheckin,
    AfterCheckin,
    BeforeCheckout,
    AfterCheckout,

    CheckinOccurred,
    CheckoutOccurred,

    OnlineCheckinCompleted,
    IdVerificationCompleted,

    AlloggiatiReceiptArrived,

    GuestMessageReceived,
    ReviewReceived,

    PaymentSucceeded,
    PaymentFailed,

    ActivityCompleted,

    WeeklyRecurrence
}
