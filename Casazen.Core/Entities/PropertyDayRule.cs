using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// The price and the rules of one night of a property (E08/F05/US01/T01, NF-36, LST-10): the price of that night, the shortest and the longest stay
/// that may start on it, whether a stay may start or end on it, and how far ahead of it a stay may be booked. One row per property and stay date
/// (unique index <see cref="PropertyDateIndexName"/>), written by the host from the calendar and read by the quote, the checkout, the public
/// calendar and the search of the booking site.
/// </summary>
/// <remarks>
/// <para><b>A row exists only while a value is set.</b> Every value is optional (<c>null</c> = no rule of its own for that night: the nightly
/// rate, the weekend surcharge or a seasonal price still decide the price, the minimum stay of the property still decides the minimum); a row
/// with no value at all is deleted by whoever clears the last one (<c>IPropertyDayRuleService.ApplyAsync</c>).</para>
/// <para><b>The date is a calendar date of the property.</b> <see cref="StayDate"/> is the night that starts that calendar day, a date without a time
/// or a zone; it is never converted. «Today» of a property, which decides whether a night is already over, comes from the time zone of the
/// property (<c>DayRuleRules.TodayOf</c>).</para>
/// <para>The database refuses a price outside 0.01 to 100000, a stay outside 1 to 365 nights, an advance outside 0 to 730 days and a maximum
/// below its minimum (<see cref="ValuesCheckName"/>). No personal data: the user is the opaque Auth0 subject.</para>
/// </remarks>
[Table("PropertyDayRules")]
public class PropertyDayRule : ITenantOwned
{
    /// <summary>Name of the unique index on (<see cref="PropertyId"/>, <see cref="StayDate"/>): a night has one row.</summary>
    public const string PropertyDateIndexName = "UIX_PropertyDayRules_PropertyId_StayDate";

    /// <summary>Name of the check constraint that bounds the values and keeps every maximum at or above its minimum.</summary>
    public const string ValuesCheckName = "CK_PropertyDayRules_Values";

    /// <summary>Longest <see cref="UpdatedByUserId"/>: the column length of <c>User.Id</c>, the Auth0 subject.</summary>
    public const int UserIdMaxLength = 255;

    // The bounds of the values, in one place: the check constraint of the table is written from them and so are the rules of
    // the service (DayRuleLimits), so a change of one cannot leave the other behind.

    /// <summary>The lowest price of a night (one cent).</summary>
    public const decimal MinPrice = 0.01m;

    /// <summary>The highest price of a night, the range of the nightly rate.</summary>
    public const decimal MaxPrice = 100000m;

    /// <summary>The shortest stay a rule may ask for, in nights.</summary>
    public const int MinStayNights = 1;

    /// <summary>The longest stay a rule may ask for, in nights.</summary>
    public const int MaxStayNights = 365;

    /// <summary>The shortest advance a rule may ask for, in days.</summary>
    public const int MinAdvance = 0;

    /// <summary>The longest advance a rule may ask for, in days.</summary>
    public const int MaxAdvance = 730;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row, copied from the property (TN-2). Server-set, never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property (FK, CASCADE: the rules go with it).</summary>
    public Guid PropertyId { get; set; }

    /// <summary>The night that starts on this calendar day of the property: a date without a time, never converted.</summary>
    public DateOnly StayDate { get; set; }

    /// <summary>The price of the night, between 0.01 and 100000 with two decimals; <c>null</c> = no price of its own for the night.</summary>
    [Precision(18, 2)]
    public decimal? Price { get; set; }

    /// <summary>The fewest nights of a stay that starts on this night (1 to 365); <c>null</c> = the minimum of the property.</summary>
    public int? MinNights { get; set; }

    /// <summary>The most nights of a stay that starts on this night (1 to 365, not below <see cref="MinNights"/>); <c>null</c> = no maximum.</summary>
    public int? MaxNights { get; set; }

    /// <summary>A stay cannot start on this night.</summary>
    public bool ClosedToArrival { get; set; }

    /// <summary>A stay cannot end on this day (the day of the departure).</summary>
    public bool ClosedToDeparture { get; set; }

    /// <summary>The fewest days ahead of this night a stay may be booked (0 to 730); <c>null</c> = no minimum.</summary>
    public int? MinAdvanceDays { get; set; }

    /// <summary>The most days ahead of this night a stay may be booked (0 to 730, not below <see cref="MinAdvanceDays"/>); <c>null</c> = no maximum.</summary>
    public int? MaxAdvanceDays { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who changed the row last: the Auth0 subject of the account; <c>null</c> for a change of the system.</summary>
    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
