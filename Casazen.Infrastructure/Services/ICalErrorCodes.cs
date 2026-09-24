using Casazen.Infrastructure.Http;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stable codes of the iCal import errors (FD-16). They are what the API returns and what the sync stores in
/// <c>PropertyICalFeed.LastError</c> / <c>SupplierProfile.CalendarSyncError</c>, instead of exception messages
/// (which would tell a user what the server can reach). Each code has a resource key in
/// <c>Casazen.Web/Resources/SharedResources*.resx</c> (<see cref="MessageKey"/>). Never rename a code.
/// </summary>
public static class ICalErrorCodes
{
    /// <summary>Not an https URL on an allowed port, or a host that is internal or a non-public IP.</summary>
    public const string InvalidUrl = "ical_invalid_url";

    /// <summary>The feed could not be downloaded (DNS, connection, HTTP status, timeout, refused destination or redirect).</summary>
    public const string Unreachable = "ical_unreachable";

    /// <summary>The feed is larger than the download limit.</summary>
    public const string TooLarge = "ical_too_large";

    /// <summary>The downloaded content is not a usable iCalendar feed.</summary>
    public const string InvalidFormat = "ical_invalid_format";

    /// <summary>The feed was downloaded but the calendar could not be updated (or a legacy error without code).</summary>
    public const string SyncFailed = "ical_sync_failed";

    private static readonly Dictionary<string, string> MessageKeys = new(StringComparer.Ordinal)
    {
        [InvalidUrl] = "ICalInvalidUrl",
        [Unreachable] = "ICalUnreachable",
        [TooLarge] = "ICalTooLarge",
        [InvalidFormat] = "ICalInvalidFormat",
        [SyncFailed] = "ICalSyncFailed",
    };

    public static IReadOnlyCollection<string> All => MessageKeys.Keys;

    /// <summary>
    /// Code to show for a stored error: the code itself, or <see cref="SyncFailed"/> for anything else (messages
    /// saved before FD-16 are never shown). Null when there is no error.
    /// </summary>
    public static string? Normalize(string? storedError)
    {
        if (string.IsNullOrWhiteSpace(storedError))
            return null;

        return MessageKeys.ContainsKey(storedError) ? storedError : SyncFailed;
    }

    /// <summary>Resource key of the message of <paramref name="code"/> (a code returned by <see cref="Normalize"/>).</summary>
    public static string MessageKey(string code) =>
        MessageKeys.TryGetValue(code, out var key) ? key : MessageKeys[SyncFailed];

    /// <summary>
    /// Code for a failed download. A refused destination or redirect is reported as <see cref="Unreachable"/>, like a
    /// DNS failure, so the error does not reveal which internal names exist.
    /// </summary>
    public static string FromFetchFailure(ExternalFetchFailure failure) => failure switch
    {
        ExternalFetchFailure.InvalidUrl => InvalidUrl,
        ExternalFetchFailure.TooLarge => TooLarge,
        _ => Unreachable,
    };
}
