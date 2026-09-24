namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stable codes of the requests that manage the import feeds of a property (PC-11). Unlike
/// <see cref="ICalErrorCodes"/> they are never stored on a feed. Each code has a resource key in
/// <c>Casazen.Web/Resources/SharedResources*.resx</c>. Never rename a code.
/// </summary>
public static class ICalFeedErrorCodes
{
    /// <summary>No feed with that id on that property (or the property is not visible). HTTP 404.</summary>
    public const string NotFound = "ical_feed_not_found";

    /// <summary>The property already has <c>ICalImport:MaxFeedsPerProperty</c> feeds. HTTP 422.</summary>
    public const string LimitReached = "ical_feed_limit_reached";

    /// <summary>The property already imports the same URL. HTTP 409.</summary>
    public const string Duplicate = "ical_feed_duplicate";

    /// <summary>The label has control characters. HTTP 400.</summary>
    public const string InvalidLabel = "ical_feed_invalid_label";

    public const string NotFoundMessageKey = "ICalFeedNotFound";
    public const string LimitReachedMessageKey = "ICalFeedLimitReached";
    public const string DuplicateMessageKey = "ICalFeedDuplicate";
    public const string InvalidLabelMessageKey = "ICalFeedInvalidLabel";
}
