using Casazen.Core.Entities;

namespace Casazen.Core.Options;

/// <summary>
/// The <c>Octorate:ChannelActions</c> section (E08/F02/US02/T01, D8, NF-75, NF-76): which channels let a channel manager change or cancel a
/// confirmed reservation that came from them. The answer is not known for any channel (D8 is unconfirmed), so <b>every flag is <c>false</c> by
/// default</b> and a channel that has no entry here is closed. A channel is opened only after a check on the sandbox; the booking then goes through
/// Octorate first and is changed in CasaZen only when Octorate confirms. Nothing here is required and nothing fails the startup.
/// </summary>
public sealed class OctorateChannelActionsOptions
{
    public const string SectionName = "Octorate:ChannelActions";

    public OctorateChannelActionOptions Airbnb { get; set; } = new();

    public OctorateChannelActionOptions BookingCom { get; set; } = new();

    public OctorateChannelActionOptions Expedia { get; set; } = new();

    public OctorateChannelActionOptions Vrbo { get; set; } = new();

    public OctorateChannelActionOptions TripAdvisor { get; set; } = new();

    public OctorateChannelActionOptions Agoda { get; set; } = new();

    /// <summary>The entry of <paramref name="source"/>; <c>null</c> for a source that has none (a portal with no value of its own, a source that is no channel): it is closed.</summary>
    public OctorateChannelActionOptions? EntryOf(BookingSource source) => source switch
    {
        BookingSource.Airbnb => Airbnb,
        BookingSource.BookingCom => BookingCom,
        BookingSource.Expedia => Expedia,
        BookingSource.Vrbo => Vrbo,
        BookingSource.TripAdvisor => TripAdvisor,
        BookingSource.Agoda => Agoda,
        _ => null,
    };
}

/// <summary>
/// What one channel lets the host do from CasaZen (<c>Octorate:ChannelActions:{Channel}</c>): both flags are <c>false</c> by default. The two
/// guidance keys are keys of <c>SharedResources</c> (Italian and English): the one sentence that says how to ask the guest or the channel. Left
/// empty they are the keys of the channel (<c>OtaChannelGuidanceKeys</c>).
/// </summary>
public sealed class OctorateChannelActionOptions
{
    /// <summary>The channel lets the host change the reservation from CasaZen.</summary>
    public bool CanModify { get; set; }

    /// <summary>The channel lets the host cancel the reservation from CasaZen.</summary>
    public bool CanCancel { get; set; }

    /// <summary>Key of the sentence that says how to ask for a change on the channel.</summary>
    public string? ModifyGuidanceKey { get; set; }

    /// <summary>Key of the sentence that says how to ask for a cancellation on the channel.</summary>
    public string? CancelGuidanceKey { get; set; }
}
