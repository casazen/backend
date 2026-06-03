using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IAuth0ManagementService
{
    Task AssignRoleAsync(string userId, UserRole role);
}
