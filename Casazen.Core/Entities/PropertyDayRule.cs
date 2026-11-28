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
    public const string PropertyDateIndexName = "UIX_PropertyDayRules_PropertyId_StayDate";

    public const string ValuesCheckName = "CK_PropertyDayRules_Values";

    public const int UserIdMaxLength = 255;

    public const decimal MinPrice = 0.01m;

    public const decimal MaxPrice = 100000m;

    public const int MinStayNights = 1;

    public const int MaxStayNights = 365;

    public const int MinAdvance = 0;

    public const int MaxAdvance = 730;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid PropertyId { get; set; }

    public DateOnly StayDate { get; set; }

    [Precision(18, 2)]
    public decimal? Price { get; set; }

    public int? MinNights { get; set; }

    public int? MaxNights { get; set; }

    public bool ClosedToArrival { get; set; }

    public bool ClosedToDeparture { get; set; }

    public int? MinAdvanceDays { get; set; }

    public int? MaxAdvanceDays { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(UserIdMaxLength)]
    public string? UpdatedByUserId { get; set; }
}
