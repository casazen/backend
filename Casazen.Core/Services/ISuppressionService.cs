namespace Casazen.Core.Services;

/// <summary>
/// Service for managing marketing email suppression list (NF-05, ADR-007-R18, R22).
/// Uses SHA-256 blind index to check suppression without storing plaintext emails.
/// </summary>
public interface ISuppressionService
{
    /// <summary>
    /// Checks if an email address is suppressed for the given organization.
    /// Uses SHA-256 blind index comparison.
    /// </summary>
    /// <param name="orgId">Organization ID</param>
    /// <param name="email">Email address to check (will be normalized)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the email is suppressed, false otherwise</returns>
    Task<bool> IsSuppressedAsync(Guid orgId, string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an email address to the suppression list (one-click unsubscribe, GDPR request, manual).
    /// Idempotent: if the hash already exists, no duplicate is created.
    /// </summary>
    /// <param name="orgId">Organization ID</param>
    /// <param name="email">Email address to suppress (will be normalized and hashed)</param>
    /// <param name="source">Source of suppression: "unsubscribe", "manual", "gdpr"</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task</returns>
    Task AddToSuppressionListAsync(Guid orgId, string email, string source, CancellationToken cancellationToken = default);
}
