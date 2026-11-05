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
}
