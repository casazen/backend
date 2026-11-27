namespace Casazen.Core.Services;

public interface IUserAuthorizationCache
{
    void Invalidate(string userId);
}
