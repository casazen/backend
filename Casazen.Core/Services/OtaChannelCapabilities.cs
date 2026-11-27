using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>What the host may do in a conversation of one channel, for one kind of thread (<see cref="OtaChannelCapabilities.For"/>).</summary>
/// <param name="CanReply">The host may answer the guest through the channel.</param>
/// <param name="ContactDetailsAllowed">A link, an email address or a phone number may be written in the answer (false before the booking on Airbnb, OCT-11).</param>
/// <param name="AttachmentsConfirmed">The channel is confirmed to take an attachment of the host (D3: not confirmed for any channel yet).</param>
/// <param name="AutoReplyConfirmed">The channel is confirmed to take an automatic answer (D4: not confirmed for any channel yet).</param>
public readonly record struct OtaChannelCapability(
    bool CanReply,
    bool ContactDetailsAllowed,
    bool AttachmentsConfirmed,
    bool AutoReplyConfirmed)
{
    public static OtaChannelCapability None => default;
}

/// <summary>
/// The one table of what the host may do in the conversation of a channel (E05/F02/US01/T01, OCT-11, D3, D4), pure: the thread detail of the
/// hub gives it to the screens as <c>allowedActions</c>, the rules of the reply (F02/US02) and the gate of the automatic answers (F05) read it
/// from here, so the three never disagree. Closed by default: a channel or a kind that is not in the table can do nothing.
/// </summary>
/// <remarks>
/// <para><b>Request.</b> A conversation with no reservation (an inquiry before the booking) is answered like the others, except on Airbnb,
/// which does not let the host write a link, an address or a number before the booking (OCT-11): <see cref="OtaChannelCapability.ContactDetailsAllowed"/>
/// is false for an Airbnb Request.</para>
/// <para><b>Unconfirmed policies.</b> Whether a channel takes an attachment of the host (D3) and an automatic answer (D4) is not confirmed for any
/// channel, so both are false everywhere. When the product owner confirms a channel, the answer changes in this one place
/// (<see cref="AttachmentsPolicyConfirmed"/>, <see cref="AutoReplyPolicyConfirmed"/>, or a row of the table), never in a screen.</para>
/// </remarks>
public static class OtaChannelCapabilities
{
    public const bool AttachmentsPolicyConfirmed = false;

    public const bool AutoReplyPolicyConfirmed = false;

    public static IReadOnlyList<OtaThreadKind> Kinds { get; } = [OtaThreadKind.Reservation, OtaThreadKind.Request];

    public static IReadOnlyList<OtaChannel> Channels { get; } = [OtaChannel.Airbnb, OtaChannel.Booking, OtaChannel.Vrbo, OtaChannel.Expedia];

    private static readonly IReadOnlyDictionary<(OtaChannel Channel, OtaThreadKind Kind), OtaChannelCapability> Table = Build();

    public static OtaChannelCapability For(OtaChannel channel, OtaThreadKind kind) =>
        Table.TryGetValue((channel, kind), out var capability) ? capability : OtaChannelCapability.None;

    public static OtaChannelCapability Direct { get; } = new(
        CanReply: true,
        ContactDetailsAllowed: true,
        AttachmentsConfirmed: AttachmentsPolicyConfirmed,
        AutoReplyConfirmed: AutoReplyPolicyConfirmed);

    private static Dictionary<(OtaChannel, OtaThreadKind), OtaChannelCapability> Build()
    {
        var table = new Dictionary<(OtaChannel, OtaThreadKind), OtaChannelCapability>();
        foreach (var channel in Channels)
        {
            foreach (var kind in Kinds)
            {
                table[(channel, kind)] = new OtaChannelCapability(
                    CanReply: true,
                    ContactDetailsAllowed: !(channel == OtaChannel.Airbnb && kind == OtaThreadKind.Request),
                    AttachmentsConfirmed: AttachmentsPolicyConfirmed,
                    AutoReplyConfirmed: AutoReplyPolicyConfirmed);
            }
        }

        return table;
    }
}
