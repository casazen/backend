namespace Casazen.Core.Suppliers;

/// <summary>
/// The two lists a host reads its requests in (LR-08a, <c>view</c> of <c>GET api/long-rent/service-requests</c>): the requests still to
/// follow and the ones that are over. <see cref="Closed"/> is what <see cref="ServiceRequestStateMachine.IsFinal"/> says (rejected,
/// paid, cancelled: nothing leaves those statuses); <see cref="Open"/> is every other status, a completed request that waits to be
/// paid included. No view means every request.
/// </summary>
public enum ServiceRequestHostView
{
    Open,
    Closed,
}
