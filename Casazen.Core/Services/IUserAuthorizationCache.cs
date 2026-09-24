namespace Casazen.Core.Services;

/// <summary>
/// Short-lived cache of the per-user data read on every authenticated request
/// (active flag, role, supplier link, context memberships, onboarding and consents). Call <see cref="Invalidate"/> whenever
/// one of those values changes so the next request reads fresh data.
/// </summary>
public interface IUserAuthorizationCache
{
    void Invalidate(string userId);
}
