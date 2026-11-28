using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class PropertyCleaningSetupRules
{
    public const int MaxBulkProperties = 200;

    public const string CleanerRequired = "cleaning_setup_cleaner_required";

    public const string CleanerMismatch = "cleaning_setup_cleaner_mismatch";

    public const string PriceInvalid = "cleaning_setup_price_invalid";

    public const string CurrencyRequired = "cleaning_setup_currency_required";

    public const string CurrencyMismatch = "cleaning_setup_currency_mismatch";

    public const string StartTimeInvalid = "cleaning_setup_start_time_invalid";

    public const string DurationInvalid = "cleaning_setup_duration_invalid";

    public const string SupplierCurrencyUnsupported = "cleaning_setup_supplier_currency_unsupported";

    public const string SupplierNotFound = "cleaning_setup_supplier_not_found";

    public const string SupplierInactive = "cleaning_setup_supplier_inactive";

    public const string SupplierOutsideComune = "cleaning_setup_supplier_outside_comune";

    public const string StaffInvalid = "cleaning_setup_staff_invalid";

    public const string BulkEmpty = "cleaning_setup_bulk_empty";

    public const string BulkTooMany = "cleaning_setup_bulk_too_many";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        "CleaningSetupCleanerRequired",
        "CleaningSetupCleanerMismatch",
        "CleaningSetupPriceInvalid",
        "CleaningSetupCurrencyRequired",
        "CleaningSetupCurrencyMismatch",
        "CleaningSetupSupplierCurrencyUnsupported",
        "CleaningSetupStartTimeInvalid",
        "CleaningSetupDurationInvalid",
        "CleaningSetupSupplierNotFound",
        "CleaningSetupSupplierInactive",
        "CleaningSetupSupplierOutsideComune",
        "CleaningSetupStaffInvalid",
        "CleaningSetupBulkEmpty",
        "CleaningSetupBulkTooMany",
    ];
}

/// <summary>
/// What a caller says of the cleaner of a property, to save it: the whole setup (what the body leaves out is cleared, as the other setups of a property do).
/// Nothing here is trusted: the service validates the whole setup that results.
/// </summary>
/// <param name="CleanerKind">Who cleans; required.</param>
/// <param name="SupplierOrgId">The supplier org, for a supplier only.</param>
/// <param name="StaffUserId">The member of the org, for staff only.</param>
/// <param name="PriceCents">The price agreed for one cleaning, in cents; required (from 1 cent) for a supplier, optional for staff.</param>
/// <param name="Currency">
/// The currency of the price (ISO 4217), required with a price and equal to the currency of the property: a number is never applied to a currency the
/// caller did not state.
/// </param>
/// <param name="StartTime">The wall-clock time the cleaning starts on the check-out day (whole minutes); <c>null</c> = the check-out time of the property.</param>
/// <param name="DurationMinutes">How long it lasts (5 to 1,440); <c>null</c> = two hours.</param>
/// <param name="Active">Whether the automation uses the setup (a new one: yes).</param>
public sealed record CleaningSetupInput(
    PropertyCleanerKind? CleanerKind,
    Guid? SupplierOrgId,
    string? StaffUserId,
    int? PriceCents,
    string? Currency,
    TimeOnly? StartTime,
    int? DurationMinutes,
    bool Active = true);

/// <summary>The cleaner of a property as the API answers it.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Configured">The property has a setup (active or not); when <c>false</c> every other member but the defaults is empty.</param>
/// <param name="CleanerKind">Who cleans.</param>
/// <param name="SupplierOrgId">The supplier org.</param>
/// <param name="SupplierName">The legal name of the supplier.</param>
/// <param name="SupplierIsTrusted">The supplier is one of the org's trusted suppliers for this property (a preference, not a rule).</param>
/// <param name="StaffUserId">The member of the staff.</param>
/// <param name="PriceCents">The agreed price, in cents of <paramref name="Currency"/>, gross as the cleaner asks it.</param>
/// <param name="Currency">The currency of the property, the one of the price.</param>
/// <param name="StartTime">The start time of the setup; <c>null</c> = the default one.</param>
/// <param name="EffectiveStartTime">The time the cleaning really starts: the setup's, else the check-out time of the property, else 10:00.</param>
/// <param name="DurationMinutes">The duration of the setup; <c>null</c> = the default one.</param>
/// <param name="EffectiveDurationMinutes">How long the cleaning really lasts.</param>
/// <param name="Active">Whether the automation uses the setup.</param>
/// <param name="UpdatedAt">When the setup was saved last (UTC).</param>
public sealed record PropertyCleaningSetupView(
    Guid PropertyId,
    bool Configured,
    PropertyCleanerKind? CleanerKind,
    Guid? SupplierOrgId,
    string? SupplierName,
    bool SupplierIsTrusted,
    string? StaffUserId,
    int? PriceCents,
    string Currency,
    TimeOnly? StartTime,
    TimeOnly EffectiveStartTime,
    int? DurationMinutes,
    int EffectiveDurationMinutes,
    bool Active,
    DateTime? UpdatedAt);

public sealed record CleaningSetupBulkItem(
    Guid PropertyId,
    bool Saved,
    string? Code,
    string? MessageKey,
    IReadOnlyList<object> MessageArgs);

public interface IPropertyCleaningSetupService
{
    Task<PropertyCleaningSetupView> GetAsync(Guid orgId, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the setup of a property of <paramref name="orgId"/>: validates it whole and replaces what the property had. An active setup needs a cleaner that can
    /// work now: a supplier with an active profile that covers the comune of the property (the checks of a service request), a member of the staff who is active
    /// and reaches the property. An inactive one is only kept (so a supplier suspended since never stops the host from switching the automation off).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>property_not_found</c>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">A code of <see cref="PropertyCleaningSetupRules"/>, or <c>property_not_bookable_in_long_mode</c> (a long-rent property has no stays).</exception>
    Task<PropertyCleaningSetupView> SaveAsync(
        Guid orgId,
        Guid propertyId,
        CleaningSetupInput input,
        string actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the same setup for many properties of <paramref name="orgId"/> (at most <see cref="PropertyCleaningSetupRules.MaxBulkProperties"/>; a repeated id counts
    /// once; none at all is 422, never «every property»). Each property is saved on its own, as <see cref="SaveAsync"/> would: one that cannot be saved (a supplier
    /// that does not cover its comune, a currency that is not its own) comes back with its code and does not stop the others, and a property that is not one the
    /// caller reaches (of another org, or outside <paramref name="scope"/>, deleted, unknown) is <c>property_not_found</c> for it, whatever the others are.
    /// The body is checked once as a whole: a request that is not valid for any property (no cleaner, no price) is refused before anything is saved.
    /// </summary>
    /// <param name="scope">The reach of the caller on the properties of the org (<c>InScope</c>): a collaborator «Solo alcuni» saves only the properties it was given.</param>
    Task<IReadOnlyList<CleaningSetupBulkItem>> SaveBulkAsync(
        Guid orgId,
        HostScope scope,
        IReadOnlyCollection<Guid> propertyIds,
        CleaningSetupInput input,
        string actorUserId,
        CancellationToken cancellationToken = default);
}
