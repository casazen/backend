using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Properties;

/// <summary>
/// The body of <c>PUT api/properties/{id}/cleaning-setup</c> (E24/F01/US01/T01, NF-120): the whole setup of the default cleaner of a property, what the body leaves out is
/// cleared. Nothing is trusted: the service validates the whole setup that results, with the codes of <see cref="PropertyCleaningSetupRules"/>.
/// </summary>
public class SaveCleaningSetupRequest
{
    /// <summary>Who cleans: <c>Supplier</c> or <c>Staff</c>. Required.</summary>
    public PropertyCleanerKind? CleanerKind { get; set; }

    /// <summary>The supplier org, for a supplier only: an org with an active supplier profile that covers the comune of the property, preferably one of the org's trusted suppliers.</summary>
    public Guid? SupplierOrgId { get; set; }

    /// <summary>The member of the org (<c>userId</c> of the members' list), for staff only: an active member who reaches the property.</summary>
    public string? StaffUserId { get; set; }

    /// <summary>
    /// The price agreed for one cleaning, in cents, gross as the cleaner asks it (no VAT split): required, from 1 cent, for a supplier; optional for staff (a member whose work has
    /// no cost to recharge leaves it out).
    /// </summary>
    public int? PriceCents { get; set; }

    /// <summary>The currency of the price (ISO 4217): required with a price, and the one of the property (<c>EUR</c> or <c>CHF</c>).</summary>
    public string? Currency { get; set; }

    /// <summary>The time the cleaning starts on the check-out day, on the clock of the property, in whole minutes (<c>11:00:00</c>); left out = the check-out time of the property.</summary>
    public TimeOnly? StartTime { get; set; }

    /// <summary>How long the cleaning lasts, in minutes (5 to 1,440); left out = 120.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Whether the automation uses the setup; <c>false</c> keeps it without using it (default <c>true</c>).</summary>
    public bool Active { get; set; } = true;

    public CleaningSetupInput ToInput() => new(CleanerKind, SupplierOrgId, StaffUserId, PriceCents, Currency, StartTime, DurationMinutes, Active);
}

/// <summary>
/// The body of <c>PUT api/properties/cleaning-setup/bulk</c> (E24/F01/US01/T01): one setup for many properties of the org. <see cref="PropertyIds"/> lists them (at most
/// <see cref="PropertyCleaningSetupRules.MaxBulkProperties"/>; none is 422, never «every property»).
/// </summary>
public sealed class BulkSaveCleaningSetupRequest : SaveCleaningSetupRequest
{
    /// <summary>The properties to save the setup for, of the caller's org and inside its reach.</summary>
    public List<Guid>? PropertyIds { get; set; }
}

/// <summary>The default cleaner of a property as the API answers it (<see cref="PropertyCleaningSetupView"/>).</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Configured">The property has a setup (active or not); when <c>false</c> only the defaults are filled.</param>
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
public sealed record PropertyCleaningSetupDto(
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
    DateTime? UpdatedAt)
{
    public static PropertyCleaningSetupDto From(PropertyCleaningSetupView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new PropertyCleaningSetupDto(
            view.PropertyId,
            view.Configured,
            view.CleanerKind,
            view.SupplierOrgId,
            view.SupplierName,
            view.SupplierIsTrusted,
            view.StaffUserId,
            view.PriceCents,
            view.Currency,
            view.StartTime,
            view.EffectiveStartTime,
            view.DurationMinutes,
            view.EffectiveDurationMinutes,
            view.Active,
            view.UpdatedAt);
    }
}

/// <summary>What the bulk save did for one property: saved, or the <c>code</c> and the localized <c>message</c> of the rule that stopped it.</summary>
public sealed class CleaningSetupBulkResultDto
{
    public Guid PropertyId { get; set; }

    public bool Saved { get; set; }

    /// <summary>The stable code of the rule that stopped the property; <c>null</c> when it was saved.</summary>
    public string? Code { get; set; }

    /// <summary>The localized text of <see cref="Code"/> (the language of the caller); <c>null</c> when it was saved.</summary>
    public string? Message { get; set; }
}

/// <summary>The answer of <c>PUT api/properties/cleaning-setup/bulk</c>: one result for each property, in the order received, and the counts. It is 200 even when some failed: never «all saved» when one did not.</summary>
public sealed class CleaningSetupBulkResponse
{
    public List<CleaningSetupBulkResultDto> Results { get; set; } = [];

    public int Saved { get; set; }

    public int Failed { get; set; }
}
