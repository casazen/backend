namespace Casazen.Core.Suppliers;

/// <summary>
/// JSON names of the fields of a service (<c>api/supplier/services</c>), as they appear in <c>fields</c> of a 422 of the
/// catalog (<see cref="SupplierServiceRuleException.Fields"/>). A supplement is named by its position:
/// <c>supplements[1].amountCents</c>.
/// </summary>
public static class SupplierServiceFields
{
    public const string Name = "name";
    public const string Category = "category";
    public const string Summary = "summary";
    public const string Description = "description";
    public const string PriceFromCents = "priceFromCents";
    public const string PriceUnit = "priceUnit";
    public const string DurationMinutes = "durationMinutes";
    public const string MinNoticeHours = "minNoticeHours";
    public const string Weekdays = "weekdays";
    public const string Supplements = "supplements";
    public const string Included = "included";
    public const string Excluded = "excluded";
    public const string PhotoUrls = "photoUrls";
    public const string SortOrder = "sortOrder";
}
