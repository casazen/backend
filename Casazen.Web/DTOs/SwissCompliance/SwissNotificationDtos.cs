using System.ComponentModel.DataAnnotations;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs.SwissCompliance;

/// <summary>
/// Answer of <c>GET /api/properties/{propertyId}/swiss/notification-day</c> (E31/F02/US01/T01, NF-131, casazen/backend#979): one arrival day of a property in a canton other than Ticino whose
/// guests the host notifies to the cantonal authority. CasaZen transmits nothing: the answer is the data ready to copy onto the official portal, what is still missing, the regime (where
/// to go, how sure the source is) and the deadline. The enums are written by name; every sentence is in the language of the request.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Day">The arrival day.</param>
/// <param name="RegimeCode">The regime of the place of the property (<c>CH-SH</c>): the one a declaration of the day carries.</param>
/// <param name="CantonCode">The two letters of the canton.</param>
/// <param name="PortalUrl">The official portal of the authority where the host makes the notification; <c>null</c> where the canton has none (a paper form, an e-mailed file).</param>
/// <param name="Channel">How the data reach the authority as the sources describe it: <c>PortalForm</c>, <c>PortalFileUpload</c>, <c>VendorInterface</c>, <c>EmailFile</c> or <c>Paper</c>.</param>
/// <param name="DeadlineKind">When the data are due, as the sources say it: <c>AtArrival</c>, <c>DailyByTime</c>, <c>Weekly</c> or <c>None</c> (a regime with no deadline, or none the sources give).</param>
/// <param name="DeadlineNote">The sentence that says what the deadline is when its kind alone does not, and where the source leaves a doubt (Schaffhausen: the time of the day is not tied to a day by the brochure).</param>
/// <param name="Certainty">How far the sources of the regime are to be trusted: <c>Verified</c>, <c>Partial</c> or <c>Unverified</c>. The page never shows as certain what the source gives as partial.</param>
/// <param name="EffectiveDuty">The duty CasaZen treats as in effect: only <c>Required</c> raises the day in the cockpit and in the alerts; <c>Ask</c> and <c>NotRequired</c> never do.</param>
/// <param name="StayCount">The stays that arrive on the day and owe a notification.</param>
/// <param name="Guests">The guests of those stays as data ready to copy, in the order of the stays.</param>
/// <param name="Issues">What the guests (or the stays) still lack: the codes <c>firstName</c>, <c>lastName</c>, <c>birthDate</c>, <c>nationality</c>, <c>documentType</c> and <c>guests</c> (fewer guests recorded than the booking counts); the clients translate them.</param>
/// <param name="DueAt">The deadline (UTC); <c>null</c> when the regime has none: a deadline is never guessed.</param>
/// <param name="DeclaredAt">When a person of the org declared the day notified (UTC); <c>null</c> while nobody did.</param>
/// <param name="DeclaredBy">The display name (first and last name) of the person who declared it; <c>null</c> while nobody did, and when the account no longer exists.</param>
/// <param name="AlreadyDeclared">The day is closed: it is declared, so the cockpit and the alerts no longer list it.</param>
/// <param name="PendingDays">The days of the property still to notify (of the last 14), oldest first; empty unless the duty is <c>Required</c>.</param>
public sealed record SwissNotificationDayResponse(
    Guid PropertyId,
    DateOnly Day,
    string RegimeCode,
    string? CantonCode,
    string? PortalUrl,
    GuestRegistrationChannel Channel,
    GuestRegistrationDeadlineKind DeadlineKind,
    string? DeadlineNote,
    GuestRegistrationCertainty Certainty,
    GuestRegistrationEffectiveDuty EffectiveDuty,
    int StayCount,
    IReadOnlyList<SwissNotificationGuestResponse> Guests,
    IReadOnlyList<SwissNotificationIssueResponse> Issues,
    DateTime? DueAt,
    DateTime? DeclaredAt,
    string? DeclaredBy,
    bool AlreadyDeclared,
    IReadOnlyList<SwissPendingDayResponse> PendingDays)
{
    public static SwissNotificationDayResponse From(SwissNotificationDay day, IStringLocalizer<SharedResources> localizer)
    {
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(localizer);

        var regime = day.Regime;
        return new SwissNotificationDayResponse(
            day.PropertyId,
            day.Day,
            regime.Code,
            regime.CantonCode,
            regime.PortalUrl,
            regime.Channel,
            regime.DeadlineKind,
            regime.DeadlineNoteKey is null ? null : localizer[regime.DeadlineNoteKey].Value,
            regime.Certainty,
            day.EffectiveDuty,
            day.StayCount,
            day.Guests.Select(SwissNotificationGuestResponse.From).ToList(),
            day.Issues.Select(i => new SwissNotificationIssueResponse(i.BookingId, i.Position, i.GuestName, i.Missing)).ToList(),
            day.DueAtUtc,
            day.DeclaredAtUtc,
            day.DeclaredBy,
            day.AlreadyDeclared,
            day.PendingDays.Select(p => new SwissPendingDayResponse(p.Day, p.StayCount, p.DueAtUtc)).ToList());
    }
}

/// <summary>
/// A guest of the day as the host copies it onto the portal: what the check-in recorded and nothing else (no exemption, no photograph of the document, no contact of the booker). A field that
/// is not there is <c>null</c>. The answer holds personal data: it asks <c>guest.read</c> and is never cached.
/// </summary>
/// <param name="BookingId">The stay.</param>
/// <param name="BookingCode">The code of the booking as hosts read it (<c>XXXXX-XXXXX</c>).</param>
/// <param name="Position">Order of the guest in the stay, from 0.</param>
/// <param name="Arrival">The arrival day of the stay.</param>
/// <param name="Departure">The departure day of the stay.</param>
/// <param name="FirstName">First name.</param>
/// <param name="LastName">Last name.</param>
/// <param name="BirthDate">Date of birth.</param>
/// <param name="PlaceOfBirth">The comune of birth (born in Italy) or the country of birth (born abroad), as entered.</param>
/// <param name="Nationality">The nationality as the guest entered it.</param>
/// <param name="NationalityCode">The nationality as ISO 3166-1 alpha-3, where CasaZen can tell it.</param>
/// <param name="DocumentType"><c>passport</c>, <c>identityCard</c> or <c>other</c>.</param>
/// <param name="DocumentNumber">The number of the document.</param>
/// <param name="Street">Street and number of the home.</param>
/// <param name="PostalCode">Postal code of the home.</param>
/// <param name="City">Town of the home.</param>
/// <param name="ResidenceCountryCode">The country of the home as ISO alpha-3.</param>
/// <param name="Plate">The plate of the car of the stay.</param>
public sealed record SwissNotificationGuestResponse(
    Guid BookingId,
    string? BookingCode,
    int Position,
    DateOnly Arrival,
    DateOnly Departure,
    string? FirstName,
    string? LastName,
    DateOnly? BirthDate,
    string? PlaceOfBirth,
    string? Nationality,
    string? NationalityCode,
    string? DocumentType,
    string? DocumentNumber,
    string? Street,
    string? PostalCode,
    string? City,
    string? ResidenceCountryCode,
    string? Plate)
{
    public static SwissNotificationGuestResponse From(SwissNotificationGuest guest)
    {
        ArgumentNullException.ThrowIfNull(guest);
        return new SwissNotificationGuestResponse(
            guest.BookingId,
            guest.BookingCode,
            guest.Position,
            guest.Arrival,
            guest.Departure,
            guest.FirstName,
            guest.LastName,
            guest.BirthDate,
            guest.PlaceOfBirth,
            guest.Nationality,
            guest.NationalityCode,
            guest.DocumentType,
            guest.DocumentNumber,
            guest.Street,
            guest.PostalCode,
            guest.City,
            guest.ResidenceCountryCode,
            guest.Plate);
    }
}

/// <summary>A guest (or a whole stay, <c>position</c> null) that still lacks something, and the codes of what it lacks.</summary>
public sealed record SwissNotificationIssueResponse(Guid BookingId, int? Position, string GuestName, IReadOnlyList<string> Missing);

/// <summary>An arrival day still to notify; <c>dueAt</c> is <c>null</c> where the regime has no deadline.</summary>
public sealed record SwissPendingDayResponse(DateOnly Day, int StayCount, DateTime? DueAt);

/// <summary>
/// Body of <c>POST /api/properties/{propertyId}/swiss/notification-declare</c> (E31/F02/US01/T01, NF-131, decision D10): the arrival day the host notified to the authority.
/// </summary>
public sealed class DeclareSwissNotificationRequest
{
    /// <summary>The arrival day, a calendar day (<c>yyyy-MM-dd</c>) in Switzerland (Europe/Zurich), not after today.</summary>
    [Required(ErrorMessage = "SwissArrivalDayRequired")]
    public DateOnly? ArrivalDay { get; set; }
}

/// <summary>
/// The declaration of an arrival day, answer of <c>POST …/notification-declare</c>: the same answer on every repeat of the call. No guest data: the day response carries the guests and asks
/// <c>guest.read</c>; this one only says that the day is declared, by whom and when.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="ArrivalDay">The arrival day.</param>
/// <param name="RegimeCode">The regime the day was declared under.</param>
/// <param name="DeclaredAt">When it was declared (UTC): the instant of the first declaration, whoever repeats the call.</param>
/// <param name="DeclaredBy">The display name (first and last name) of the person who declared it; <c>null</c> when the account no longer exists.</param>
/// <param name="Created"><c>true</c> when this call recorded the declaration (the other hosts were told); <c>false</c> when the day was declared already: the answer is then the one that was there.</param>
public sealed record SwissDeclarationResponse(Guid PropertyId, DateOnly ArrivalDay, string RegimeCode, DateTime DeclaredAt, string? DeclaredBy, bool Created)
{
    public static SwissDeclarationResponse From(SwissDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return new SwissDeclarationResponse(
            declaration.PropertyId, declaration.Day, declaration.RegimeCode, declaration.DeclaredAtUtc, declaration.DeclaredBy, declaration.Created);
    }
}
