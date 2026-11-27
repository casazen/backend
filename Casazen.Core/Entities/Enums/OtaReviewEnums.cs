namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where an action CasaZen asks of the channel manager on a review stands: the host's reply to the review of the guest, or the host's own review of the
/// guest (E05/F06/US01/T01, OCT-12, OCT-13). Stored as an integer.
/// </summary>
/// <remarks>
/// There is no state for «on its way»: the row is saved as <see cref="ManualVerify"/> <b>before</b> the call leaves (the outcome of a call that is running is not
/// known either), and the answer turns it into <see cref="Sent"/> or <see cref="Error"/>. A process that dies in between leaves exactly the state it should:
/// the action may or may not have reached the channel, and only a read of the channel, never a second send, decides it.
/// </remarks>
public enum OtaReviewActionState
{
    /// <summary>Nothing was asked of the channel manager (or the attempt did not leave and was taken back).</summary>
    None = 0,

    /// <summary>The channel manager took it, or a read of it shows it there: the reply is published, the review of the host is sent.</summary>
    Sent = 1,

    /// <summary>The channel manager refused it with an answer CasaZen understood: the code is on the row, and the host may try again with a new key.</summary>
    Error = 2,

    /// <summary>The outcome is not known (no answer, a 5xx, an answer that cannot be read): a person or the next read checks the channel, and the call is never sent again by itself (OCT-13).</summary>
    ManualVerify = 3,
}

/// <summary>Whether the host may still write its own review of the guest on the channel (the window of Airbnb), as the channel manager says it. Stored as an integer.</summary>
public enum HostReviewWindow
{
    /// <summary>The channel manager did not say (or was not read yet): nothing is allowed until it does.</summary>
    Unknown = 0,

    /// <summary>The host may write its review now.</summary>
    Open = 1,

    /// <summary>The host's review is on the channel already.</summary>
    Sent = 2,

    /// <summary>The window is over: the review can no longer be written.</summary>
    Closed = 3,
}
