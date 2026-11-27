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
    public static readonly DateTime PlaceholderDate = new(3000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public const decimal PlaceholderRent = 0m;

    public const LeaseContractType PlaceholderType = LeaseContractType.Libero;

    public const int FirstStep = 1;

    public const int LastDataStep = 5;

    public const int LastStep = 8;

    public static bool IsPlaceholder(DateTime date) => date.Date == PlaceholderDate.Date;
}
