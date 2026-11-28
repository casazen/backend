namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stable codes of the requests that manage the import feeds of a property (PC-11). Unlike
/// <see cref="ICalErrorCodes"/> they are never stored on a feed. Each code has a resource key in
/// <c>Casazen.Web/Resources/SharedResources*.resx</c>. Never rename a code.
/// </summary>
public static class ICalFeedErrorCodes
{
    public const string NotFound = "ical_feed_not_found";

    public const string LimitReached = "ical_feed_limit_reached";

    public const string Duplicate = "ical_feed_duplicate";

    public const string InvalidLabel = "ical_feed_invalid_label";

    public const string SupplierNoFeed = "ical_supplier_no_feed";

    public const string NotFoundMessageKey = "ICalFeedNotFound";
    public const string SupplierNoFeedMessageKey = "ICalSupplierNoFeed";
    public const string LimitReachedMessageKey = "ICalFeedLimitReached";
    public const string DuplicateMessageKey = "ICalFeedDuplicate";
    public const string InvalidLabelMessageKey = "ICalFeedInvalidLabel";
}
