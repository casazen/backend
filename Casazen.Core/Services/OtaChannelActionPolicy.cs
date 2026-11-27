using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;

namespace Casazen.Core.Services;

/// <summary>
/// The keys of <c>SharedResources</c> (Italian and English) of the one sentence that says how to ask the guest or the channel for a change or a
/// cancellation of a booking that came from a channel (E08/F02/US02/T01, NF-75, NF-76): one for each channel and each action, and a neutral one
/// for a portal that has no sentence of its own. The configuration may name another key (<see cref="OctorateChannelActionOptions"/>); without it
/// these are the keys.
/// </summary>
public static class OtaChannelGuidanceKeys
{
    /// <summary>Start of the key of the sentence about changing a booking.</summary>
    public const string ModifyPrefix = "OtaChannelModifyGuidance";

    /// <summary>Start of the key of the sentence about cancelling a booking.</summary>
    public const string CancelPrefix = "OtaChannelCancelGuidance";

    /// <summary>The channels that have a sentence of their own: the others use the neutral one (<c>Default</c>).</summary>
    public static IReadOnlyList<BookingSource> Channels { get; } =
    [
        BookingSource.Airbnb,
        BookingSource.BookingCom,
        BookingSource.Expedia,
        BookingSource.Vrbo,
        BookingSource.TripAdvisor,
        BookingSource.Agoda,
    ];

    /// <summary>The key of the sentence about changing a booking of <paramref name="source"/>.</summary>
    public static string ModifyOf(BookingSource source) => ModifyPrefix + SuffixOf(source);

    /// <summary>The key of the sentence about cancelling a booking of <paramref name="source"/>.</summary>
    public static string CancelOf(BookingSource source) => CancelPrefix + SuffixOf(source);

    private static string SuffixOf(BookingSource source) => Channels.Contains(source) ? source.ToString() : "Default";
}

/// <summary>What one channel lets the host do from CasaZen, with the sentence that says how to ask for it where it does not.</summary>
/// <param name="Source">The channel.</param>
/// <param name="CanModify">The host may change the reservation from CasaZen.</param>
/// <param name="CanCancel">The host may cancel the reservation from CasaZen.</param>
/// <param name="ModifyGuidanceKey">Key of the sentence about changing it on the channel.</param>
/// <param name="CancelGuidanceKey">Key of the sentence about cancelling it on the channel.</param>
public sealed record OtaChannelActionRule(
    BookingSource Source,
    bool CanModify,
    bool CanCancel,
    string ModifyGuidanceKey,
    string CancelGuidanceKey)
{
    /// <summary>The name of the channel as the API says it: the name of its <see cref="BookingSource"/> (<c>BookingCom</c>).</summary>
    public string Channel => Source.ToString();
}

/// <summary>
/// Which channels let the host change or cancel a confirmed reservation that came from them (E08/F02/US02/T01, D8, NF-75, NF-76). D8 is
/// unconfirmed, so the answer is a configuration (<see cref="OctorateChannelActionsOptions"/>) and it is closed by default: a channel that is
/// not listed, and a source that is no channel, are closed. One place for the answer: the guards of the services, the cancellation of a channel
/// booking, the quote of the cancellation and the change options of a booking all ask it.
/// </summary>
public sealed class OtaChannelActionPolicy(OctorateChannelActionsOptions options)
{
    /// <summary>Name of the member of the problem that carries the key of the sentence.</summary>
    public const string GuidanceKeyExtension = "guidanceKey";

    /// <summary>Name of the member of the problem that carries the channel.</summary>
    public const string ChannelExtension = "channel";

    /// <summary>The policy of a configuration with no entry at all: every channel closed.</summary>
    public static OtaChannelActionPolicy Closed { get; } = new(new OctorateChannelActionsOptions());

    /// <summary>
    /// True for a booking that came from a channel through the channel manager: the source is a channel (the one definition of the code,
    /// <see cref="FiscalCopy.IsOtaBookingSource"/>) and the reservation is on Octorate. A stay made from the iCal feed of a channel has no
    /// reservation and is CasaZen's own record: it is not one.
    /// </summary>
    public static bool IsChannelBooking(BookingSource source, string? octorateReservationId) =>
        FiscalCopy.IsOtaBookingSource(source) && !string.IsNullOrWhiteSpace(octorateReservationId);

    /// <summary>True for a booking that came from a channel through the channel manager (see <see cref="IsChannelBooking(BookingSource, string?)"/>).</summary>
    public static bool IsChannelBooking(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return IsChannelBooking(booking.Source, booking.OctorateReservationId);
    }

    /// <summary>The rule of <paramref name="source"/>: both flags are as configured, <c>false</c> when the channel has no entry.</summary>
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

    /// <summary>
    /// 422 <see cref="BookingErrorCodes.OtaBookingChangeNotAllowed"/>: the booking is changed on its channel, not here. The text is the sentence
    /// of the channel; <c>guidanceKey</c> and <c>channel</c> travel with the problem for the screens.
    /// </summary>
    public DomainRuleException ChangeNotAllowed(BookingSource source)
    {
        var rule = For(source);
        return Refusal(BookingErrorCodes.OtaBookingChangeNotAllowed, rule, rule.ModifyGuidanceKey);
    }

    /// <summary>422 <see cref="BookingErrorCodes.OtaBookingCancelNotAllowed"/>: the booking is cancelled on its channel, not here.</summary>
    public DomainRuleException CancelNotAllowed(BookingSource source)
    {
        var rule = For(source);
        return Refusal(BookingErrorCodes.OtaBookingCancelNotAllowed, rule, rule.CancelGuidanceKey);
    }

    /// <summary>
    /// 422 <see cref="BookingErrorCodes.OctorateCancelFailed"/>: Octorate did not cancel the reservation of a channel booking, so the booking
    /// is as it was. The text is the sentence that says how to ask on the channel.
    /// </summary>
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
