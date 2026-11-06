using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Options;

/// <summary>
/// The <c>Octorate:ExistingInventory</c> section (E02/F01/US03): the import of listings that already exist on OTAs or in another channel
/// manager (D6). It holds the evidence of the real-property pilot, channel by channel, and the pace of the check of the channel access.
/// Nothing here is required and nothing fails the startup: with the defaults no D6 operation is called, every step that needs one stops with
/// the code <c>d6_operation_unverified</c> and stays guided (docs/runbooks/octorate-existing-inventory-import.md).
/// </summary>
/// <remarks>
/// <para><b>Fail closed.</b> The four operations of the D6 sequence (<c>IMPORT_ROOMS</c>, <c>IMPORT_RESERVATIONS</c>, <c>IMPORT_CALENDAR</c>,
/// <c>MARK_CONNECTED</c>) are documented by Octorate but were never run on a real property: their exact behaviour is a per-channel fact
/// that only the pilot settles. A flag is turned on for one channel only after the pilot recorded that operation for that channel in the
/// runbook; a channel or an operation that is not listed is not verified.</para>
/// </remarks>
public sealed class OctorateExistingInventoryOptions
{
    public const string SectionName = "Octorate:ExistingInventory";

    /// <summary>How many times the access to a channel is checked before the person may confirm it by hand, when the setting is not given.</summary>
    public const int DefaultAccessCheckMaxAttempts = 6;

    /// <summary>Seconds before the first check of an access, doubled at each check, when the setting is not given.</summary>
    public const int DefaultAccessCheckBackoffSeconds = 30;

    /// <summary>The longest wait between two checks of an access, in seconds.</summary>
    public const int MaxAccessCheckBackoffSeconds = 600;

    /// <summary>The most checks of an access that may be asked for.</summary>
    public const int MaxAccessCheckMaxAttempts = 20;

    /// <summary>Checks of the access to a channel before it is offered for the person's confirmation (1 to <see cref="MaxAccessCheckMaxAttempts"/>).</summary>
    public int AccessCheckMaxAttempts { get; set; } = DefaultAccessCheckMaxAttempts;

    /// <summary>Seconds before the first check of an access; the wait doubles at each check up to <see cref="MaxAccessCheckBackoffSeconds"/>.</summary>
    public int AccessCheckBackoffSeconds { get; set; } = DefaultAccessCheckBackoffSeconds;

    /// <summary>The capability flags of each channel, keyed by the portal code (for example <c>142</c>), compared without regard to case.</summary>
    public Dictionary<string, OctorateExistingInventoryChannelOptions> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The checks of the access, brought into their ranges.</summary>
    public int EffectiveAccessCheckMaxAttempts =>
        AccessCheckMaxAttempts is >= 1 and <= MaxAccessCheckMaxAttempts ? AccessCheckMaxAttempts : DefaultAccessCheckMaxAttempts;

    /// <summary>The first wait of the checks of an access, brought into 1 to <see cref="MaxAccessCheckBackoffSeconds"/> seconds.</summary>
    public int EffectiveAccessCheckBackoffSeconds =>
        AccessCheckBackoffSeconds is >= 1 and <= MaxAccessCheckBackoffSeconds ? AccessCheckBackoffSeconds : DefaultAccessCheckBackoffSeconds;

    /// <summary>
    /// The wait after the <paramref name="attempt"/>-th check of an access (from 1): the first wait, doubled at each check, never longer than
    /// <see cref="MaxAccessCheckBackoffSeconds"/>.
    /// </summary>
    public TimeSpan BackoffAfter(int attempt)
    {
        var seconds = (double)EffectiveAccessCheckBackoffSeconds;
        for (var i = 1; i < attempt && seconds < MaxAccessCheckBackoffSeconds; i++)
            seconds *= 2;

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxAccessCheckBackoffSeconds));
    }

    /// <summary>
    /// True when <paramref name="operation"/> was verified on a real property for the channel <paramref name="channelCode"/> and its flag is on.
    /// A channel that is not listed, an empty code and an operation that is not a member of <see cref="OctorateD6Operation"/> are not verified.
    /// </summary>
    public bool IsVerified(string? channelCode, OctorateD6Operation operation)
    {
        var code = channelCode?.Trim();
        if (string.IsNullOrEmpty(code) || !Channels.TryGetValue(code, out var channel) || channel is null)
            return false;

        return operation switch
        {
            OctorateD6Operation.ImportRooms => channel.ImportRooms,
            OctorateD6Operation.ImportReservations => channel.ImportReservations,
            OctorateD6Operation.ImportCalendar => channel.ImportCalendar,
            OctorateD6Operation.MarkConnected => channel.MarkConnected,
            _ => false,
        };
    }
}

/// <summary>
/// The evidence of the real-property pilot for one channel (<c>Octorate:ExistingInventory:Channels:{code}</c>): which operations of the D6
/// sequence were run and recorded in the runbook. All off by default.
/// </summary>
public sealed class OctorateExistingInventoryChannelOptions
{
    /// <summary><c>IMPORT_ROOMS</c> was verified for this channel (<c>Octorate:ExistingInventory:Channels:{code}:ImportRooms</c>).</summary>
    public bool ImportRooms { get; set; }

    /// <summary><c>IMPORT_RESERVATIONS</c> was verified for this channel (<c>Octorate:ExistingInventory:Channels:{code}:ImportReservations</c>).</summary>
    public bool ImportReservations { get; set; }

    /// <summary><c>IMPORT_CALENDAR</c> was verified for this channel (<c>Octorate:ExistingInventory:Channels:{code}:ImportCalendar</c>).</summary>
    public bool ImportCalendar { get; set; }

    /// <summary><c>MARK_CONNECTED</c> was verified for this channel (<c>Octorate:ExistingInventory:Channels:{code}:MarkConnected</c>).</summary>
    public bool MarkConnected { get; set; }
}
