namespace Casazen.Core.Services;

/// <summary>
/// The organization an Octorate call is made for (ADR-023-R4). No method of <see cref="IOctorateApiClient"/> takes an org: every call
/// acts for <see cref="OrgId"/>, and the token, the connection and the rate-limit bucket all come from it, so a caller cannot make a
/// call with another org's connection by passing the wrong id.
/// </summary>
/// <remarks>
/// Inside <see cref="Use"/> the org is that one: a job or an event handler, which works for an org with no request, opens it first
/// (<c>using (scope.Use(orgId)) { ... }</c>). Otherwise it is the org of the authenticated request. With neither, reading
/// <see cref="OrgId"/> throws: no call is ever made for no org (fail closed).
/// </remarks>
public interface IOctorateOrgScope
{
    Guid OrgId { get; }

    IDisposable Use(Guid orgId);
}
