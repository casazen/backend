using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What each alert stage leaves behind besides its email and its push (E03/F02/US02/T01, NF-48, NF-52): the "To do" item of the condition and, for a
/// legal deadline within 24 hours, the popup alert. Pure: the same stage always gives the same request, so a retry meets the item it opened.
/// </summary>
/// <remarks>
/// <para><b>One item, whoever raises it.</b> The type and the action of the "To do" are the ones the feed of the cockpit raises for the same cockpit
/// action (<see cref="ComplianceActivityFeeder.TodoOf"/>), on the same entity (the stay, the property, or the property and the month), so an item
/// opened here is the one <see cref="ComplianceActivityFeeder"/> finds, and the other way round: the open-item index keeps one. The reason is the
/// stable code of the traffic light that says the same thing (<see cref="ComplianceReasonCodes"/>), never a text and never personal data.</para>
/// <para><b>Before the arrival is a warning, from the arrival on it is urgent.</b> The data of the guests missing and the stay over 30 nights the
/// day before are <see cref="ActivityUrgency.Medium"/>; what is due now is <see cref="ActivityUrgency.High"/>, as the cockpit paints it red.</para>
/// </remarks>
internal static class ComplianceFollowUps
{
    /// <summary>The types of "To do" the stay stages raise: the sweeps of the stays that owe nothing and of the conditions that cleared look for these.</summary>
    public static readonly ActivityType[] StayTodoTypes =
    [
        ActivityType.CheckInPending,
        ActivityType.AlloggiatiDeadline,
        ActivityType.ReceiptToRetrieve,
    ];

    /// <summary>The "To do" of a stay alert stage; <c>null</c> for a stage that has none (the check-out reminder).</summary>
    public static CreateTodoRequest? TodoForStay(Booking booking, StayAlertStep step)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(step);

        var deadline = step.Alert.DeadlineUtc ?? StayAlertSchedule.AlertDeadlineUtc(booking);
        return step.Alert.Kind switch
        {
            // The data of the guests are missing before the arrival: the check-in of the booking is where the host completes them.
            StayAlertKind.GuestDataMissing => ForStay(
                booking,
                ActivityType.CheckInPending,
                ActivityAction.StartCheckIn,
                ActivityUrgency.Medium,
                AlloggiatiTerms.ArrivalDayStartUtc(booking.CheckInDate),
                ComplianceReasonCodes.GuestDataMissing),
            StayAlertKind.AlloggiatiDeadlineApproaching => FromCockpit(
                booking, ComplianceCockpitAction.SendAlloggiati, ActivityUrgency.High, deadline, ComplianceReasonCodes.AlloggiatiNotSent),
            StayAlertKind.AlloggiatiOverdue => FromCockpit(
                booking, ComplianceCockpitAction.SendAlloggiati, ActivityUrgency.High, deadline, ComplianceReasonCodes.DeadlineOverdue),
            StayAlertKind.AlloggiatiFailed => FromCockpit(
                booking, ComplianceCockpitAction.ResolveAlloggiatiFailure, ActivityUrgency.High, deadline, ComplianceReasonCodes.AlloggiatiFailed),
            StayAlertKind.ReceiptMissing => FromCockpit(
                booking, ComplianceCockpitAction.UploadAlloggiatiReceipt, ActivityUrgency.High, deadline, ComplianceReasonCodes.ReceiptMissing),
            StayAlertKind.ShortStayAtRisk => FromCockpit(
                booking, ComplianceCockpitAction.SendAlloggiatiManually, ActivityUrgency.High, deadline, ComplianceReasonCodes.ShortStayAtRisk),
            StayAlertKind.OverThirtyNights => FromCockpit(
                booking, ComplianceCockpitAction.SendAlloggiatiManually, ActivityUrgency.Medium, deadline, ComplianceReasonCodes.OverThirtyNightsNotSent),
            _ => null,
        };
    }

    /// <summary>
    /// The popup alert of a stay alert stage, or <c>null</c> when the stage has no legal deadline (the data of the guests, the receipt, a failed
    /// communication, the check-out). Whether the deadline is within 24 hours is decided when it is raised
    /// (<see cref="IComplianceAlertFollowUp.PopupAsync"/>): the later reminders, days after the deadline, never reach it.
    /// </summary>
    public static ComplianceAlertPopup? PopupForStay(Booking booking, StayAlertStep step)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(step);

        if (step.Alert.Kind is not (StayAlertKind.AlloggiatiDeadlineApproaching
            or StayAlertKind.AlloggiatiOverdue
            or StayAlertKind.ShortStayAtRisk
            or StayAlertKind.OverThirtyNights))
        {
            return null;
        }

        return new ComplianceAlertPopup(
            booking.OrgId,
            booking.PropertyId,
            AlertType.AlloggiatiDeadline,
            PopupAlert.BookingEntityType,
            booking.Id,
            step.Alert.DeadlineUtc ?? StayAlertSchedule.AlertDeadlineUtc(booking));
    }

    /// <summary>The "To do" of an ISTAT or tourist tax period stage: about the property and the month, with the cockpit action that fixes it.</summary>
    public static CreateTodoRequest? TodoForPeriod(ComplianceAlertStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var tourist = step.Type == ComplianceAlertType.TouristTaxPeriod;
        var action = tourist ? ComplianceCockpitAction.DeclareTouristTax : ComplianceCockpitAction.UploadIstatProof;
        if (ComplianceActivityFeeder.TodoOf(action) is not { } mapping || step.Alert.DeadlineDay is not { } deadlineDay)
            return null;

        var overdue = step.Alert.Kind == ComplianceAlertKind.PeriodOverdue;
        return new CreateTodoRequest
        {
            Type = mapping.Type,
            Action = mapping.Action,
            EntityType = ComplianceActivityFeeder.PeriodEntityTypePrefix + step.ReferenceKey,
            EntityId = step.Alert.PropertyId,
            Urgency = overdue ? ActivityUrgency.High : ActivityUrgency.Medium,
            PropertyId = step.Alert.PropertyId,
            Deadline = ComplianceAlertSchedule.DeadlineEndUtc(deadlineDay),
            Reason = overdue ? ComplianceReasonCodes.DeadlineOverdue : ComplianceReasonCodes.PeriodOpen,
        };
    }

    /// <summary>The popup alert of a period stage: the property, with the deadline that ends the deadline day. ISTAT is a legal alert; the tourist tax is a compliance issue.</summary>
    public static ComplianceAlertPopup PopupForPeriod(Guid orgId, ComplianceAlertStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new ComplianceAlertPopup(
            orgId,
            step.Alert.PropertyId,
            step.Type == ComplianceAlertType.IstatPeriod ? AlertType.IstatDeadline : AlertType.ComplianceIssue,
            PopupAlert.PropertyEntityType,
            step.Alert.PropertyId,
            step.Alert.DeadlineDay is { } day ? ComplianceAlertSchedule.DeadlineEndUtc(day) : null);
    }

    /// <summary>The "To do" of an inactive connection: the property, with the cockpit action that opens the page of its sender.</summary>
    public static CreateTodoRequest? TodoForConnection(Guid propertyId)
    {
        if (ComplianceActivityFeeder.TodoOf(ComplianceCockpitAction.ActivateOctorateSend) is not { } mapping)
            return null;

        return new CreateTodoRequest
        {
            Type = mapping.Type,
            Action = mapping.Action,
            EntityType = ComplianceActivityFeeder.PropertyEntityType,
            EntityId = propertyId,
            Urgency = ActivityUrgency.High,
            PropertyId = propertyId,
            Reason = ComplianceReasonCodes.ConnectionInactive,
        };
    }

    private static CreateTodoRequest? FromCockpit(Booking booking, ComplianceCockpitAction action, ActivityUrgency urgency, DateTime deadline, string reason) =>
        ComplianceActivityFeeder.TodoOf(action) is { } mapping
            ? ForStay(booking, mapping.Type, mapping.Action, urgency, deadline, reason)
            : null;

    private static CreateTodoRequest ForStay(
        Booking booking,
        ActivityType type,
        ActivityAction action,
        ActivityUrgency urgency,
        DateTime deadline,
        string reason) =>
        new()
        {
            Type = type,
            Action = action,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = booking.Id,
            Urgency = urgency,
            PropertyId = booking.PropertyId,
            Deadline = deadline,
            Reason = reason,
        };
}
