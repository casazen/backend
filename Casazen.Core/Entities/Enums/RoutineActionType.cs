namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Action types for automation routines (NF-26, ADR-022).
/// Phase 1 actions as specified in AC-NF26-3.
/// </summary>
public enum RoutineActionType
{
    // Guest communication (email, fake WhatsApp/SMS in test mode)
    MessageToGuest,

    // Internal notifications
    NotifyOwner,
    NotifyStaff,
    NotifyTeam,

    // Activity creation (staff, trusted supplier, marketplace)
    CreateActivity,

    // Access code (only after ID verification, ADR-022-R11)
    SendAccessCode,

    // Review request
    RequestReview,

    // CRM tagging
    AddTag,

    // Delay/wait
    Wait
}
