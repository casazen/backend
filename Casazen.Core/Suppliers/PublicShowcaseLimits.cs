namespace Casazen.Core.Suppliers;

public static class PublicShowcaseLimits
{
    public const int SlotsDefaultDays = 14;

    public const int SlotsMaxDays = SupplierAgendaLimits.MaxCalendarDays;

    public static readonly TimeSpan SlotsCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Plans the slots cache holds at the same time. The key is a real service of a real supplier (never a value the client
    /// makes up), so the number of keys follows the catalogs; this is only the last line against a runaway.
    /// </summary>
    public const int SlotsCacheCapacity = 2_000;

    public const int ResponseTimeMinSamples = 5;

    public const int ResponseTimeMaxSamples = 500;

    public const int QuoteIncludedSurfaceSqm = 60;

    public const int QuoteSurfaceStepSqm = 30;

    public const int QuoteMaxQuantity = 1_000;

    public const int QuoteMaxSurfaceSqm = 10_000;

    public const int QuoteMaxOptions = SupplierServiceCatalogLimits.MaxSupplements;

    public const int QuoteComuneMaxLength = 100;

    public const int QuotePostalCodeMaxLength = 10;

    public const int QuoteMaxBodyBytes = 8 * 1024;

    public const int SlugMaxLength = 100;
}
