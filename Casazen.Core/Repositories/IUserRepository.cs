using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(string id);
    Task<User?> GetByEmailAsync(string email);
    Task<User?> GetBySubAsync(string sub);
    Task<IEnumerable<User>> GetAllAsync();
    Task<(IEnumerable<User> Users, int TotalCount)> GetPagedAsync(string? search, string? role, bool? isActive, int page, int pageSize);
    Task<User> AddAsync(User user);

    /// <summary>
    /// Inserts <paramref name="user"/> unless a row with the same <c>Id</c> already exists, and returns the stored
    /// row. Two first-access requests of the same Auth0 user may both try the insert (A1-14): the one that loses
    /// gets the row of the winner instead of a primary-key violation.
    /// </summary>
    Task<(User User, bool Created)> AddIfAbsentAsync(User user);
    Task UpdateAsync(User user);

    /// <summary>
    /// Sets <c>IsActive</c> of user <paramref name="id"/> (PL-03). A deactivation runs under a transaction-scoped
    /// advisory lock and changes nothing when <paramref name="actorId"/> was deactivated meanwhile
    /// (<see cref="UserActivationOutcome.ActorInactive"/>) or when the user is the last active platform admin
    /// (<see cref="UserActivationOutcome.LastActiveAdmin"/>): two admins deactivating each other at the same time
    /// cannot both succeed. Returns the tracked user (null only for <see cref="UserActivationOutcome.NotFound"/>).
    /// </summary>
    Task<(UserActivationOutcome Outcome, User? User)> SetActiveAsync(
        string id,
        bool isActive,
        string actorId,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of <see cref="IUserRepository.SetActiveAsync"/>.</summary>
public enum UserActivationOutcome
{
    /// <summary>The active flag changed.</summary>
    Updated,

    /// <summary>The user already had the requested state: nothing written.</summary>
    Unchanged,

    NotFound,

    /// <summary>Deactivation refused: the admin who asked for it is no longer active.</summary>
    ActorInactive,

    /// <summary>Deactivation refused: no other active user would keep the <see cref="UserRole.Admin"/> role.</summary>
    LastActiveAdmin,
}
