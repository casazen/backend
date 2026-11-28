using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Properties;

public class SaveCleaningSetupRequest
{
    public PropertyCleanerKind? CleanerKind { get; set; }

    public Guid? SupplierOrgId { get; set; }

    public string? StaffUserId { get; set; }

    /// <summary>
    /// The price agreed for one cleaning, in cents, gross as the cleaner asks it (no VAT split): required, from 1 cent, for a supplier; optional for staff (a member whose work has
    /// no cost to recharge leaves it out).
    /// </summary>
    public int? PriceCents { get; set; }

    public string? Currency { get; set; }

    public TimeOnly? StartTime { get; set; }

    public int? DurationMinutes { get; set; }

    public bool Active { get; set; } = true;

    public CleaningSetupInput ToInput() => new(CleanerKind, SupplierOrgId, StaffUserId, PriceCents, Currency, StartTime, DurationMinutes, Active);
}

public sealed class BulkSaveCleaningSetupRequest : SaveCleaningSetupRequest
{
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

public sealed class CleaningSetupBulkResultDto
{
    public Guid PropertyId { get; set; }

    public bool Saved { get; set; }

    public string? Code { get; set; }

    public string? Message { get; set; }
}

public sealed class CleaningSetupBulkResponse
{
    public List<CleaningSetupBulkResultDto> Results { get; set; } = [];

    public int Saved { get; set; }

    public int Failed { get; set; }
}
