using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;

namespace Casazen.Core.Services;

public static class OtaChannelGuidanceKeys
{
    public const string ModifyPrefix = "OtaChannelModifyGuidance";

    public const string CancelPrefix = "OtaChannelCancelGuidance";

    public static IReadOnlyList<BookingSource> Channels { get; } =
    [
        BookingSource.Airbnb,
        BookingSource.BookingCom,
        BookingSource.Expedia,
        BookingSource.Vrbo,
        BookingSource.TripAdvisor,
        BookingSource.Agoda,
    ];

    public static string ModifyOf(BookingSource source) => ModifyPrefix + SuffixOf(source);

    public static string CancelOf(BookingSource source) => CancelPrefix + SuffixOf(source);

    private static string SuffixOf(BookingSource source) => Channels.Contains(source) ? source.ToString() : "Default";
}

public sealed record OtaChannelActionRule(
    BookingSource Source,
    bool CanModify,
    bool CanCancel,
    string ModifyGuidanceKey,
    string CancelGuidanceKey)
{
    public string Channel => Source.ToString();
}

public sealed class OtaChannelActionPolicy(OctorateChannelActionsOptions options)
{
    public const string GuidanceKeyExtension = "guidanceKey";

    public const string ChannelExtension = "channel";

    public static OtaChannelActionPolicy Closed { get; } = new(new OctorateChannelActionsOptions());

    /// <summary>
    /// True for a booking that came from a channel through the channel manager: the source is a channel (the one definition of the code,
    /// <see cref="FiscalCopy.IsOtaBookingSource"/>) and the reservation is on Octorate. A stay made from the iCal feed of a channel has no
    /// reservation and is CasaZen's own record: it is not one.
    /// </summary>
    public static bool IsChannelBooking(BookingSource source, string? octorateReservationId) =>
        FiscalCopy.IsOtaBookingSource(source) && !string.IsNullOrWhiteSpace(octorateReservationId);

    public static bool IsChannelBooking(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return IsChannelBooking(booking.Source, booking.OctorateReservationId);
    }

    public OtaChannelActionRule For(BookingSource source)
    {
        var entry = options.EntryOf(source);
        return new OtaChannelActionRule(
            source,
            entry?.CanModify ?? false,
            entry?.CanCancel ?? false,
            KeyOrDefault(entry?.ModifyGuidanceKey, OtaChannelGuidanceKeys.ModifyOf(source)),
            KeyOrDefault(entry?.CancelGuidanceKey, OtaChannelGuidanceKeys.CancelOf(source)));
    }

    public DomainRuleException ChangeNotAllowed(BookingSource source)
    {
        var rule = For(source);
        return Refusal(BookingErrorCodes.OtaBookingChangeNotAllowed, rule, rule.ModifyGuidanceKey);
    }

    public DomainRuleException CancelNotAllowed(BookingSource source)
    {
        var rule = For(source);
        return Refusal(BookingErrorCodes.OtaBookingCancelNotAllowed, rule, rule.CancelGuidanceKey);
    }

    public DomainRuleException CancelFailed(BookingSource source)
    {
        var rule = For(source);
        return Refusal(BookingErrorCodes.OctorateCancelFailed, rule, rule.CancelGuidanceKey);
    }

    private static DomainRuleException Refusal(string code, OtaChannelActionRule rule, string guidanceKey)
    {
        return new DomainRuleException(code, guidanceKey)
        {
            Extensions = new Dictionary<string, object?>
            {
                [GuidanceKeyExtension] = guidanceKey,
                [ChannelExtension] = rule.Channel,
            },
        };
    }

    private static string KeyOrDefault(string? configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
}
