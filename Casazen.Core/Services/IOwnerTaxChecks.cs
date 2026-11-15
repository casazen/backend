using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>
/// The two checks of the Italian short-let withholding that look at the owner and at the tenant (E18/F01/US03/T01, NF-117, ⚖ I7 and I8): the limit of units per
/// owner (from the third apartment let short, counting those the owner declares outside the agency, the activity is presumed to be a business) and the company
/// tenant in the property of a private owner (the tax agency and the Court of Cassation read the regime differently, so a person decides and the decision is recorded).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Facts and warnings.</b> The facts of the withholding (<see cref="CheckUnitsAsync"/>, <see cref="TreatmentOfStaysAsync"/>) are always computed: an owner past the
/// limit is a business whatever the screens show, and a decision recorded is followed. The warnings of the UI (<see cref="WarningsEnabled"/>: the owner readiness, the
/// statement version, the «Da fare», the endpoints) are behind <c>Features:TaxReportingExports</c>.</item>
/// <item><b>The limit</b> is the configuration <c>ShortStayFiscal:MaxApartmentsPerTaxpayer</c> (2: the third unit is presumed a business, from the tax year
/// <c>ShortStayFiscal:ThresholdFromTaxYear</c>), never a number of the code.</item>
/// <item><b>Reach.</b> The web layer decides the permission and passes the caller's <see cref="HostScope"/>: an owner is inside the reach of an org-wide scope when it is an
/// owner of the org, inside a partial one only when it holds at least one ownership and every one of them is on a property the scope reaches; a booking is inside it when its
/// property is. Anything else is the 404 of a record that does not exist. The count of the units is the owner's whole portfolio in the org, whatever the reach of the caller.</item>
/// <item><b>Privacy.</b> The logs carry ids and counts. The reason of a decision is a note of the agency: never logged, never in the «Da fare». Nothing of the invoice data of the
/// tenant (name, fiscal code, VAT number, address) is read beyond its kind (private person or company) or returned.</item>
/// </list>
/// </remarks>
public interface IOwnerTaxChecks
{
    /// <summary>True while <c>Features:TaxReportingExports</c> is on: the warnings of the UI (readiness, statement flag, «Da fare», endpoints) are made.</summary>
    bool WarningsEnabled { get; }

    /// <summary>
    /// The check of the limit of units for each owner of <paramref name="orgId"/> among <paramref name="ownerIds"/> on <paramref name="asOf"/> (a Rome day): the apartments
    /// the agency manages for the owner (an Italian property in short-rent mode with an ownership of the owner in force that day) plus the ones the owner declared outside
    /// the agency. An owner that is not in the org is not in the answer; an empty list is an empty answer.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, OwnerUnitsCheck>> CheckUnitsAsync(Guid orgId, IReadOnlyCollection<Guid> ownerIds, DateOnly asOf, CancellationToken ct = default);

    /// <summary>The check of the limit of units of one owner today. 404 <c>owner_not_found</c> (also an owner outside the reach of the caller).</summary>
    Task<OwnerUnitsCheck> GetUnitsCheckAsync(HostScope scope, Guid ownerId, CancellationToken ct = default);

    /// <summary>
    /// Saves how many apartments the owner lets short outside the agency (0 to <see cref="PropertyOwner.ExternalShortLetUnitsMax"/>; required: a request that does not carry
    /// the number is 422, never a 0 by omission) and answers the new check. 404 <c>owner_not_found</c>; 422 <c>owner_external_units_invalid</c> (with the <c>field</c>); a
    /// refusal changes nothing.
    /// </summary>
    Task<OwnerUnitsCheck> DeclareExternalUnitsAsync(HostScope scope, Guid ownerId, int? units, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Records the decision about the tax regime of a stay of a company tenant and closes its «Da fare». The stay must be one of those (<see cref="OwnerTaxRules.IsCompanyTenantCase"/>:
    /// an active stay of at most <c>ShortStayFiscal:MaxStayNights</c> nights in an Italian property with a private owner, whose invoice data names a company): a regime is never
    /// recorded for a stay that does not need one. The same regime and reason again is the same decision (nothing is written twice). 404 <c>booking_not_found</c> (also a booking
    /// outside the reach); 422 <c>owner_tax_decision_invalid</c> (with the <c>field</c>), <c>owner_tax_decision_not_applicable</c>.
    /// </summary>
    Task<ShortLetTaxDecision> DecideAsync(HostScope scope, DecideShortLetTaxCommand command, string userId, CancellationToken ct = default);

    /// <summary>
    /// The stays of a company tenant in reach with their decision (the current one), the most recent check-out first, at most <see cref="OwnerTaxLimits.MaxCases"/>; with
    /// <paramref name="state"/> only the ones still waiting for a decision or only the ones decided.
    /// </summary>
    Task<IReadOnlyList<CompanyTenantCase>> ListCompanyTenantCasesAsync(HostScope scope, CompanyTenantCaseState? state, CancellationToken ct = default);

    /// <summary>
    /// Raises the «Da fare» <c>company_tenant_decision</c> of the stay when it is one of those and has no decision: once for each booking (also after a person closed it), no
    /// personal data in it. <c>false</c> when nothing was raised: the flag is off, the stay is not one of those, it was decided or the item was raised before.
    /// </summary>
    Task<bool> RaiseCompanyTenantDecisionAsync(Guid orgId, Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// The regime the withholding facts apply to the <paramref name="bookingIds"/> of <paramref name="ownerId"/> on <paramref name="asOf"/> (E18/F01/US01 reads it): a stay does
    /// not qualify as a short let when the owner is presumed a business (<see cref="CheckUnitsAsync"/>) or when a decision against it was recorded for a stay that is still one
    /// of a company tenant; a company tenant without a decision changes nothing (⚖ I7: no automatic change of regime) and says that its decision is pending. Only what this
    /// asks: the Italian property, the nights, the collection and the portal withholding are the facts' own. A booking of another org, or that does not exist, has no entry.
    /// </summary>
    Task<IReadOnlyList<StayShortLetTreatment>> TreatmentOfStaysAsync(
        Guid orgId,
        Guid ownerId,
        IReadOnlyCollection<Guid> bookingIds,
        DateOnly asOf,
        CancellationToken ct = default);
}

/// <summary>
/// The limit of units of an owner (cross-repo contract). <paramref name="Applies"/> is false for an owner that is already a business (a legal entity, or a VAT holder): the
/// limit is about natural persons. <paramref name="PresumedBusiness"/> is true when the owner applies, the limit is in force on the day and the units
/// (<paramref name="TotalUnits"/> = <paramref name="ManagedUnits"/> + <paramref name="ExternalUnits"/>) are more than <paramref name="MaxUnits"/>.
/// </summary>
public sealed record OwnerUnitsCheck(
    Guid OwnerId,
    bool Applies,
    int ManagedUnits,
    int ExternalUnits,
    int TotalUnits,
    int MaxUnits,
    bool PresumedBusiness);

/// <summary>A decision about the regime of a stay: the booking, the regime and the note. The regime is required (never one by omission).</summary>
public sealed record DecideShortLetTaxCommand
{
    /// <summary>The stay (a booking of the caller's org).</summary>
    public Guid BookingId { get; init; }

    /// <summary>The regime to apply: <see cref="ShortLetTaxRegime.ShortLet"/> or <see cref="ShortLetTaxRegime.NotShortLet"/>. Required.</summary>
    public ShortLetTaxRegime? Regime { get; init; }

    /// <summary>Why: 1 to <see cref="ShortLetTaxDecision.ReasonMaxLength"/> characters, no control characters. Required.</summary>
    public string? Reason { get; init; }
}

/// <summary>Which stays of a company tenant a list asks for.</summary>
public enum CompanyTenantCaseState
{
    /// <summary>Still waiting for a decision.</summary>
    Pending = 1,

    /// <summary>A decision was recorded (the current one is shown).</summary>
    Decided = 2,
}

/// <summary>
/// A stay of a company tenant in the property of a private owner (<c>GET api/owner-tax-decisions</c>): what the person needs to find it (the code of the booking, the
/// property, the dates) and the current decision, if any. Never the name of the tenant or its fiscal data.
/// </summary>
public sealed record CompanyTenantCase(
    Guid BookingId,
    string BookingCode,
    Guid PropertyId,
    string PropertyName,
    DateOnly CheckIn,
    DateOnly CheckOut,
    ShortLetTaxRegime? Decision,
    DateTime? DecidedAt,
    string? DecidedByUserId);

/// <summary>
/// What the withholding facts apply to one stay (<see cref="IOwnerTaxChecks.TreatmentOfStaysAsync"/>): <paramref name="QualifiesAsShortLet"/> false means no short-let
/// withholding on it for the owner, with the code in <paramref name="Reason"/> (<see cref="ShortLetTreatmentReasons"/>). <paramref name="CompanyTenant"/> is true when the
/// stay is one of a company tenant; <paramref name="DecisionPending"/> when it is and nobody decided yet (the regime is unchanged and the «Da fare» is open).
/// </summary>
public sealed record StayShortLetTreatment(
    Guid BookingId,
    bool QualifiesAsShortLet,
    string Reason,
    bool CompanyTenant,
    bool DecisionPending,
    ShortLetTaxRegime? Decision,
    DateTime? DecidedAt);

/// <summary>The codes of the reason of a <see cref="StayShortLetTreatment"/> (cross-repo contract): never rename one.</summary>
public static class ShortLetTreatmentReasons
{
    /// <summary>The stay is a short let: the default, also a company tenant nobody decided about.</summary>
    public const string ShortLet = "short_let";

    /// <summary>The owner is past the limit of units: a business, no short-let regime.</summary>
    public const string OwnerPresumedBusiness = "owner_presumed_business";

    /// <summary>A person decided that the stay of a company tenant stays a short let.</summary>
    public const string CompanyTenantShortLet = "company_tenant_short_let";

    /// <summary>A person decided that the stay of a company tenant is not a short let.</summary>
    public const string CompanyTenantNotShortLet = "company_tenant_not_short_let";
}

/// <summary>The stable codes of the checks of the owners, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class OwnerTaxErrors
{
    /// <summary>422 (with the <c>field</c>): the decision names no booking, no regime of the enum or no usable reason.</summary>
    public const string DecisionInvalid = "owner_tax_decision_invalid";

    /// <summary>422: the stay is not one of a company tenant in the property of a private owner, so it needs no decision.</summary>
    public const string DecisionNotApplicable = "owner_tax_decision_not_applicable";

    /// <summary>422 (with the <c>field</c>): the number of apartments declared outside the agency is not a count.</summary>
    public const string ExternalUnitsInvalid = "owner_external_units_invalid";

    /// <summary>404: no booking of the caller's org has that id (also one outside the reach).</summary>
    public const string BookingNotFound = "booking_not_found";
}

/// <summary>The limits of the checks of the owners.</summary>
public static class OwnerTaxLimits
{
    /// <summary>The most stays of a company tenant a list answers: the most recent ones.</summary>
    public const int MaxCases = 200;
}

/// <summary>
/// The rules of the two checks, pure (E18/F01/US03/T01): reading only what they are given, so the service, the readiness, the statement and the tests apply the same
/// definition.
/// </summary>
public static class OwnerTaxRules
{
    /// <summary>
    /// The owner is a natural person who is not a VAT holder: the one the short-let regime and its limit are about. A legal entity, or a VAT holder, is a business already.
    /// An owner whose kind is not declared yet counts as a person (the registry readiness asks for it).
    /// </summary>
    public static bool IsNaturalPrivate(OwnerEntityKind? kind, OwnerTaxRegime regime) =>
        kind != OwnerEntityKind.LegalEntity && regime == OwnerTaxRegime.Private;

    /// <summary>
    /// The check of the limit for one owner: the units are the managed ones plus the declared ones, and the activity is presumed to be a business from the one after
    /// <see cref="ShortStayFiscalOptions.MaxApartmentsPerTaxpayer"/> (never less than one), from the tax year <see cref="ShortStayFiscalOptions.ThresholdFromTaxYear"/>: a day of an
    /// earlier year was under another limit that the configuration does not carry, so it presumes nothing.
    /// </summary>
    public static OwnerUnitsCheck CheckUnits(
        Guid ownerId,
        OwnerEntityKind? kind,
        OwnerTaxRegime regime,
        int managedUnits,
        int externalUnits,
        DateOnly asOf,
        ShortStayFiscalOptions rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var applies = IsNaturalPrivate(kind, regime);
        var managed = Math.Max(0, managedUnits);
        var external = Math.Max(0, externalUnits);
        var total = managed + external;
        var max = Math.Max(1, rules.MaxApartmentsPerTaxpayer);
        var inForce = asOf.Year >= rules.ThresholdFromTaxYear;
        return new OwnerUnitsCheck(ownerId, applies, managed, external, total, max, applies && inForce && total > max);
    }

    /// <summary>
    /// The stay needs a decision about its regime: it is active (<see cref="Regulatory.AlloggiatiStatusRules.ActiveBookingStatuses"/> and no no-show: the one definition),
    /// in an Italian property, of at most <paramref name="maxStayNights"/> nights (beyond them it is no short let at all), its invoice data names a company, and one of the
    /// owners on the day is a private natural person. A stay of a company owner, or one whose guest is a private person, needs none.
    /// </summary>
    public static bool IsCompanyTenantCase(
        bool active,
        bool italianProperty,
        int nights,
        int maxStayNights,
        bool companyRecipient,
        IEnumerable<(OwnerEntityKind? Kind, OwnerTaxRegime Regime)> ownersOnTheDay)
    {
        ArgumentNullException.ThrowIfNull(ownersOnTheDay);

        return active
               && italianProperty
               && nights >= 1
               && nights <= maxStayNights
               && companyRecipient
               && ownersOnTheDay.Any(o => IsNaturalPrivate(o.Kind, o.Regime));
    }

    /// <summary>The decision in force for a stay: the latest (then the one with the greatest id, always the same). <c>null</c> without one.</summary>
    public static ShortLetTaxDecision? CurrentDecision(IEnumerable<ShortLetTaxDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        return decisions.OrderByDescending(d => d.DecidedAt).ThenByDescending(d => d.Id).FirstOrDefault();
    }

    /// <summary>
    /// What the withholding facts apply to a stay of an owner. The owner presumed a business first: no short-let regime, whatever else is said. Then the stay of a company
    /// tenant (<paramref name="companyTenantCase"/>) with a decision: the decision. Anything else, a company tenant nobody decided about included, is a short let (⚖ I7: a
    /// decision is recorded, the regime is never changed automatically), the last one with its decision pending. A decision is followed only while the stay still is one of a
    /// company tenant: one recorded for a stay whose recipient changed since is not.
    /// </summary>
    public static StayShortLetTreatment TreatmentOf(Guid bookingId, bool ownerPresumedBusiness, bool companyTenantCase, ShortLetTaxDecision? current)
    {
        if (ownerPresumedBusiness)
            return new StayShortLetTreatment(bookingId, false, ShortLetTreatmentReasons.OwnerPresumedBusiness, companyTenantCase, false, null, null);

        if (!companyTenantCase)
            return new StayShortLetTreatment(bookingId, true, ShortLetTreatmentReasons.ShortLet, false, false, null, null);

        if (current is null)
            return new StayShortLetTreatment(bookingId, true, ShortLetTreatmentReasons.ShortLet, true, true, null, null);

        var shortLet = current.Regime == ShortLetTaxRegime.ShortLet;
        return new StayShortLetTreatment(
            bookingId,
            shortLet,
            shortLet ? ShortLetTreatmentReasons.CompanyTenantShortLet : ShortLetTreatmentReasons.CompanyTenantNotShortLet,
            true,
            false,
            current.Regime,
            current.DecidedAt);
    }

    /// <summary>The nights of a stay: the days between the check-in and the check-out day.</summary>
    public static int Nights(DateTime checkIn, DateTime checkOut) => (DateOnly.FromDateTime(checkOut).DayNumber - DateOnly.FromDateTime(checkIn).DayNumber);
}
