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
    public const string InvalidUrl = "ical_invalid_url";

    public const string Unreachable = "ical_unreachable";

    public const string TooLarge = "ical_too_large";

    public const string InvalidFormat = "ical_invalid_format";

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

    public static string? Normalize(string? storedError)
    {
        if (string.IsNullOrWhiteSpace(storedError))
            return null;

        return MessageKeys.ContainsKey(storedError) ? storedError : SyncFailed;
    }

    public static string MessageKey(string code) =>
        MessageKeys.TryGetValue(code, out var key) ? key : MessageKeys[SyncFailed];

    public static string FromFetchFailure(ExternalFetchFailure failure) => failure switch
    {
        ExternalFetchFailure.InvalidUrl => InvalidUrl,
        ExternalFetchFailure.TooLarge => TooLarge,
        _ => Unreachable,
    };
}
