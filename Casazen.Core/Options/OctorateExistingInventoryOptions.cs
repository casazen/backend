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

    public const int DefaultAccessCheckMaxAttempts = 6;

    public const int DefaultAccessCheckBackoffSeconds = 30;

    public const int MaxAccessCheckBackoffSeconds = 600;

    public const int MaxAccessCheckMaxAttempts = 20;

    public int AccessCheckMaxAttempts { get; set; } = DefaultAccessCheckMaxAttempts;

    public int AccessCheckBackoffSeconds { get; set; } = DefaultAccessCheckBackoffSeconds;

    public Dictionary<string, OctorateExistingInventoryChannelOptions> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> InitialCalendarPortals { get; set; } = new(StringComparer.Ordinal);

    public int EffectiveAccessCheckMaxAttempts =>
        AccessCheckMaxAttempts is >= 1 and <= MaxAccessCheckMaxAttempts ? AccessCheckMaxAttempts : DefaultAccessCheckMaxAttempts;

    public int EffectiveAccessCheckBackoffSeconds =>
        AccessCheckBackoffSeconds is >= 1 and <= MaxAccessCheckBackoffSeconds ? AccessCheckBackoffSeconds : DefaultAccessCheckBackoffSeconds;

    public TimeSpan BackoffAfter(int attempt)
    {
        var seconds = (double)EffectiveAccessCheckBackoffSeconds;
        for (var i = 1; i < attempt && seconds < MaxAccessCheckBackoffSeconds; i++)
            seconds *= 2;

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxAccessCheckBackoffSeconds));
    }

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

public sealed class OctorateExistingInventoryChannelOptions
{
    public bool ImportRooms { get; set; }

    public bool ImportReservations { get; set; }

    public bool ImportCalendar { get; set; }

    public bool MarkConnected { get; set; }

    public bool CommitMapping { get; set; }
}
