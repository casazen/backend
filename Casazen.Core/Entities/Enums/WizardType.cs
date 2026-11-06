namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The wizards whose progress is kept as a draft the person finds again on any device (<see cref="WizardDraft"/>, ADR-024). The
/// column stores the number, so every value has an explicit one: a later task (E03, E10) appends a value with the next number, an
/// existing number never changes and is never reused. The API speaks the member name (<c>api/wizards/{type}/draft</c>).
/// </summary>
public enum WizardType
{
    ListingCreate = 1,
    ListingEdit = 2,
    BookingCreate = 3,
    BookingEdit = 4,
    CheckIn = 5,
    CheckOut = 6,
    ComplianceSender = 7,
    TouristTax = 8,

    /// <summary>The activation of CasaZen with Octorate (E10/F01, NF-104): the plan of the eleven parts and its steps, resumed where the person left off.</summary>
    OnboardingActivation = 9,

    /// <summary>The bulk configuration of an agency's properties (E10/F02, NF-105): the choices applied to all or to a selection, and the per-property table.</summary>
    OnboardingBulk = 10,
}
