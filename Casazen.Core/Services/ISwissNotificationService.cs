using Casazen.Core.Regulatory.GuestRegistration;

namespace Casazen.Core.Services;

/// <summary>
/// The assisted notification of the guests to the cantonal authority of a canton other than Ticino (E31/F02/US01/T01, NF-131, decisions D1, D5, D12 and D14,
/// casazen/backend#979): CasaZen never transmits and never logs into a portal. For a Swiss property whose canton asks for an electronic notification of the guests
/// (<see cref="SwissAssistedNotification.Covers"/>) and whose duty is <see cref="GuestRegistrationEffectiveDuty.Required"/> (the host confirmed it, or the regime is verified as
/// covering private rentals), it lists the arrival days still to notify with their deadline, gives the guests of a day as data ready to copy onto the official portal, and records
/// that the host declared the day notified. Ticino has its own service (<see cref="ITicinoNotificationService"/>) and never appears here.
/// </summary>
/// <remarks>
/// <para><b>The one rule of a closed day.</b> A day is closed by a declaration of the same property, day and regime
/// (<c>SwissNotificationDeclaration.RegimeCode</c> = the code of the regime): <see cref="ListPendingDaysAsync"/> is the one place that says it, so the cockpit and the alerts read
/// only that list and a declared day stops both at once. A declaration of another regime (the property changed canton) or of the notification to the Ticino police closes nothing.</para>
/// <para><b>Behind <c>Features:GuestRegistrationRegimes</c>.</b> With the flag off the list is empty: no item, no alert. The day, the declaration and its undo are for the
/// web layer to put behind the same flag (404 before authentication); the service does not read the host's setting while it is off.</para>
/// <para><b>Tenant and scope.</b> Every read names the org of the property, so a context with its tenant filter off (the hourly job) never mixes orgs. The service never reads the caller:
/// the web layer decides the org and the reach, and says who declares.</para>
/// </remarks>
public interface ISwissNotificationService
{
    /// <summary>How many days back the days still to notify are looked for.</summary>
    public const int PendingLookbackDays = SwissAssistedNotification.PendingLookbackDays;

    /// <summary>
    /// One arrival day of a property the assisted notification covers: the guests of the stays that arrive on it as data ready to copy, what they lack, the regime (the portal, the
    /// deadline and how sure the source is), when the day is due, who declared it notified and when, and the other days of the property still to notify.
    /// Throws <c>property_not_found</c> (no such property in the org), or <c>swiss_notification_not_applicable</c> (422: the property is not Swiss, is in Ticino, or its canton asks
    /// only for a register or is not known). It does not need the duty to be <c>Required</c>: it says what the duty is, and only a <c>Required</c> day is raised.
    /// </summary>
    Task<SwissNotificationDay> GetDayAsync(Guid propertyId, DateOnly day, CancellationToken cancellationToken = default);

    /// <summary>
    /// The arrival days still to notify of the properties of an org, or of every org when <paramref name="orgId"/> is <c>null</c>, from <see cref="PendingLookbackDays"/> days
    /// before <paramref name="today"/> up to it, oldest first: the stays arrived (an active status and no no-show: the one definition of a stay that owes a notification) of the
    /// properties whose regime the assisted notification covers and whose duty is <c>Required</c>, on days no declaration closes. <paramref name="propertyIds"/> limits the properties
    /// read (the caller's reach); <c>null</c> reads them all and an empty list reads none, never «all». Empty while <c>Features:GuestRegistrationRegimes</c> is off.
    /// </summary>
    Task<IReadOnlyList<SwissPendingDay>> ListPendingDaysAsync(
        Guid? orgId,
        IReadOnlyCollection<Guid>? propertyIds,
        DateOnly today,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Declares an arrival day as notified to the authority, on the portal or by the way the canton asks (decision D10): the day is closed (<see cref="ListPendingDaysAsync"/> no
    /// longer lists it) and shows who declared it and when. <b>Idempotent</b>: a day declared already answers the declaration that exists (<see cref="SwissDeclaration.Created"/>
    /// false), whoever repeats the call, and nothing is told again. A created declaration is told live to the hosts of the property (<c>swiss-notification-declared</c>).
    /// Throws what <see cref="GetDayAsync"/> throws and <c>swiss_declaration_day_in_future</c> (422: a day that has not started in Switzerland cannot be declared: it would silence
    /// the alerts of an arrival that has not happened).
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="day">The arrival day.</param>
    /// <param name="userId">The person who declares (<c>User.Id</c>, the Auth0 subject): the web layer says it, the service never reads the caller.</param>
    /// <param name="cancellationToken">Aborts the call.</param>
    Task<SwissDeclaration> DeclareDayAsync(Guid propertyId, DateOnly day, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes the declaration of an arrival day: the row is deleted and the day is to notify again. <b>Idempotent</b>: <c>false</c>, and nothing told, when the day was not declared (also
    /// when a co-host removed the declaration at the same moment). Throws what <see cref="GetDayAsync"/> throws.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="day">The arrival day.</param>
    /// <param name="userId">The person who undoes it (<c>User.Id</c>): written in the log of the application, never in the row (it is deleted).</param>
    /// <param name="cancellationToken">Aborts the call.</param>
    /// <returns><c>true</c> when a declaration was removed.</returns>
    Task<bool> UndeclareDayAsync(Guid propertyId, DateOnly day, string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An arrival day still to notify to the authority of a canton other than Ticino.
/// </summary>
/// <param name="OrgId">The org of the property.</param>
/// <param name="PropertyId">The property.</param>
/// <param name="PropertyName">The name of the property.</param>
/// <param name="RegimeCode">The code of the regime the day is notified under (<c>CH-SH</c>): the one a declaration of the day carries.</param>
/// <param name="Day">The arrival day.</param>
/// <param name="StayCount">The stays that arrive on the day and owe a notification.</param>
/// <param name="DueAtUtc">The deadline (<see cref="SwissAssistedNotification.DueAtUtc"/>); <c>null</c> when the regime has none (a deadline is never guessed).</param>
public sealed record SwissPendingDay(Guid OrgId, Guid PropertyId, string PropertyName, string RegimeCode, DateOnly Day, int StayCount, DateTime? DueAtUtc);

/// <summary>An arrival day of a property the assisted notification covers (<see cref="ISwissNotificationService.GetDayAsync"/>).</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Day">The arrival day.</param>
/// <param name="Regime">The regime of the place of the property: the portal, the deadline kind and its note, the certainty and the sources.</param>
/// <param name="EffectiveDuty">The duty CasaZen treats as in effect: only <c>Required</c> raises the day in the cockpit and in the alerts.</param>
/// <param name="StayCount">The stays that arrive on the day and owe a notification.</param>
/// <param name="Guests">The guests of those stays as data ready to copy.</param>
/// <param name="Issues">What the guests (or the stays) still lack.</param>
/// <param name="DueAtUtc">The deadline of the day; <c>null</c> when the regime has none.</param>
/// <param name="DeclaredAtUtc">When a person declared the day notified, UTC; <c>null</c> while nobody did.</param>
/// <param name="DeclaredBy">The display name of the person who declared it (first and last name of the user); <c>null</c> while nobody did, and when the account no longer exists.</param>
/// <param name="PendingDays">The days of the property still to notify, this one included when it is.</param>
public sealed record SwissNotificationDay(
    Guid PropertyId,
    DateOnly Day,
    GuestRegistrationRegime Regime,
    GuestRegistrationEffectiveDuty EffectiveDuty,
    int StayCount,
    IReadOnlyList<SwissNotificationGuest> Guests,
    IReadOnlyList<SwissNotificationIssue> Issues,
    DateTime? DueAtUtc,
    DateTime? DeclaredAtUtc,
    string? DeclaredBy,
    IReadOnlyList<SwissPendingDay> PendingDays)
{
    /// <summary>The day is closed: a person declared it notified (the rule of <see cref="ISwissNotificationService.ListPendingDaysAsync"/>).</summary>
    public bool AlreadyDeclared => DeclaredAtUtc is not null;
}

/// <summary>A declaration of an arrival day (<see cref="ISwissNotificationService.DeclareDayAsync"/>).</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Day">The arrival day.</param>
/// <param name="RegimeCode">The regime the day was declared under.</param>
/// <param name="DeclaredAtUtc">When it was declared (UTC): the instant of the first declaration, whoever repeats it.</param>
/// <param name="DeclaredBy">The display name of the person who declared it; <c>null</c> when the account no longer exists.</param>
/// <param name="Created">True when this call recorded the declaration, false when the day was declared already.</param>
public sealed record SwissDeclaration(Guid PropertyId, DateOnly Day, string RegimeCode, DateTime DeclaredAtUtc, string? DeclaredBy, bool Created);

/// <summary>The codes and message keys of the assisted notification (the answers of the 4xx).</summary>
public static class SwissNotificationErrorCodes
{
    /// <summary>422: the property is not one the assisted notification covers (not Swiss, in Ticino, a canton that asks only for a register, or a place that is not known).</summary>
    public const string NotApplicable = "swiss_notification_not_applicable";

    public const string NotApplicableMessageKey = "SwissNotificationNotApplicable";

    /// <summary>422: the day to declare has not started in Switzerland.</summary>
    public const string DeclarationDayInFuture = "swiss_declaration_day_in_future";

    public const string DeclarationDayInFutureMessageKey = "SwissDeclarationDayInFuture";
}
