namespace Casazen.Core.Services;

/// <summary>
/// Whether the channel manager (Octorate) can take a stay of a CasaZen property (E08/F01/US02/T01, NF-74, NF-80, ADR-023): asked before a booking
/// of CasaZen is written (the guest's checkout, the host's wizard) and when the host looks at the dates, besides CasaZen's own occupancy
/// (<see cref="CheckoutHolds.OccupiesDates"/>, <see cref="PropertyOccupancy"/>), which it does not replace.
/// </summary>
/// <remarks>
/// Advice, never a gate that can fail by itself: a property that is not mirrored, a channel manager that cannot be asked (timeout, 429, 5xx, a
/// room not chosen, the connector off) or one that answers something unusable gives <see cref="ChannelManagerAnswer.NotMirrored"/> or
/// <see cref="ChannelManagerAnswer.Unanswered"/>, and the caller goes on: the create push (E08/F01/US01/T03) reports a conflict if Octorate
/// refuses later. Only an answer "not available" (<see cref="ChannelManagerAnswer.NotAvailable"/>) stops a booking. The public calendar and the
/// public availability never use it: they read CasaZen's own data, so that a visitor cannot spend the quota of the org (D7).
/// </remarks>
public interface IChannelManagerAvailability
{
    /// <summary>
    /// The answer of the channel manager for the nights from <paramref name="from"/> (arrival day) to the night before <paramref name="to"/>
    /// (departure day, not a night) of <paramref name="propertyId"/>, which belongs to <paramref name="orgId"/> (the caller has already
    /// authorized the stay on it). <paramref name="guests"/> is at least one: Octorate's check takes no occupancy and the capacity stays
    /// CasaZen's own rule.
    /// </summary>
    Task<ChannelManagerAvailability> CheckAsync(Guid propertyId, Guid orgId, DateOnly from, DateOnly to, int guests, CancellationToken ct);
}

/// <summary>What the channel manager said of a stay (<see cref="IChannelManagerAvailability.CheckAsync"/>).</summary>
public enum ChannelManagerAnswer
{
    /// <summary>Nothing to ask: the property is not mirrored on the channel manager (no connected account, no confirmed mapping, the connector is off).</summary>
    NotMirrored = 0,

    /// <summary>Octorate answered that the nights can be booked.</summary>
    Available = 1,

    /// <summary>Octorate answered that the nights cannot be booked (it refuses them, or they are taken there).</summary>
    NotAvailable = 2,

    /// <summary>The property is mirrored but Octorate could not be asked (timeout, rate limit, server error, a room not chosen): no answer, no refusal.</summary>
    Unanswered = 3,
}

/// <summary>The answer of <see cref="IChannelManagerAvailability.CheckAsync"/>.</summary>
/// <param name="Answer">What was said.</param>
/// <param name="Reason">Octorate's short reason of a refusal, when it gives one; never shown to a guest.</param>
public sealed record ChannelManagerAvailability(ChannelManagerAnswer Answer, string? Reason = null)
{
    /// <summary>The property is not mirrored: nothing was asked.</summary>
    public static ChannelManagerAvailability NotMirrored { get; } = new(ChannelManagerAnswer.NotMirrored);

    /// <summary>The property is mirrored and Octorate could not be asked.</summary>
    public static ChannelManagerAvailability Unanswered { get; } = new(ChannelManagerAnswer.Unanswered);

    /// <summary>
    /// The member <c>channelManagerChecked</c> of the host availability: <c>null</c> when nothing was asked (not mirrored), <c>true</c> when
    /// Octorate answered, <c>false</c> when it could not be asked.
    /// </summary>
    public bool? Checked => Answer switch
    {
        ChannelManagerAnswer.NotMirrored => null,
        ChannelManagerAnswer.Unanswered => false,
        _ => true,
    };

    /// <summary>Octorate said the nights cannot be booked: the only answer that refuses a booking.</summary>
    public bool IsNotAvailable => Answer == ChannelManagerAnswer.NotAvailable;
}
