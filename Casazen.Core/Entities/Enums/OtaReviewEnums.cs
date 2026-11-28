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
    None = 0,

    Sent = 1,

    Error = 2,

    ManualVerify = 3,
}

public enum HostReviewWindow
{
    Unknown = 0,

    Open = 1,

    Sent = 2,

    Closed = 3,
}
