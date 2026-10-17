using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// The starting values of the columns a draft lease cannot leave empty (LR-12). <see cref="LeaseContract.StartDate"/>,
/// <see cref="LeaseContract.EndDate"/>, <see cref="LeaseContract.MonthlyRent"/> and <see cref="LeaseContract.ContractType"/> stay NOT NULL
/// because the views, the occupancy of a property, the privacy retention, the RLI reminders and the agenda read them; a draft
/// created with only a property gets these values, and a value is written into its column only when the landlord has typed a
/// valid one.
/// </summary>
/// <remarks>
/// <para><b>Why a date far in the future.</b> The readers that act on a date (the RLI and Questura reminders, the agenda, the
/// retention of the parties' data, the fiscal year of a property) must find nothing to act on in a draft whose dates were never
/// typed: a date in the future starts no registration deadline and no reminder, falls outside every agenda window and every fiscal
/// year, and is not an "ended" lease. A date in the past, or today, would do all of those. The date is obviously not a real one
/// (<c>3000-01-01</c>), so a screen can tell a starting value from a typed one, but it still leaves room for the arithmetic the
/// readers do on a date (<c>AddMonths</c>, <c>AddDays</c>).</para>
/// <para>The same readers are also told which drafts are still incomplete (<see cref="LeaseContract.DraftData"/> is not null):
/// the RLI reminders and the agenda skip them, so a draft whose start date was typed in the past does not remind anyone either.</para>
/// </remarks>
public static class LeaseDraftDefaults
{
    /// <summary>Start and end date of a draft whose dates were never typed: midnight UTC of 1 January 3000.</summary>
    public static readonly DateTime PlaceholderDate = new(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Monthly rent of a draft whose rent was never typed (0: no real rent is ever 0, the rules refuse it).</summary>
    public const decimal PlaceholderRent = 0m;

    /// <summary>The contract type of a draft whose type was never chosen: the enum default, the type most leases are.</summary>
    public const LeaseContractType PlaceholderType = LeaseContractType.Libero;

    /// <summary>
    /// The step the wizard is on when the draft is created with only the property, and the step a lease created complete in one
    /// call (<c>POST api/leases</c>) is taken to have reached: the last of the data steps.
    /// </summary>
    public const int FirstStep = 1;

    public const int LastDataStep = 5;

    public const int LastStep = 8;

    /// <summary>True when <paramref name="date"/> is the starting value of a draft, not a date somebody typed.</summary>
    public static bool IsPlaceholder(DateTime date) => date.Date == PlaceholderDate.Date;
}
