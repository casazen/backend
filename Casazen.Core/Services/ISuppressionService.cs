namespace Casazen.Core.Services;

/// <summary>
/// Service for managing marketing email suppression list (NF-05, ADR-007-R18, R22; NF-95 CRM-08).
/// The list stores an index of the address, never the address: since E07/F06/US01/T02 the index of a new entry is the keyed HMAC-SHA256 of
/// <see cref="ICrmContactIndex"/> (<c>Crm:ContactIndexKey</c>); the entries made before it hold the plain SHA-256 of the address and are
/// still honored. An entry is never removed by this service and no consent clears it: re-subscription is not specified.
/// </summary>
public interface ISuppressionService
{
    /// <summary>
    /// Whether an address can be indexed now, that is whether the keyed index has its key (<c>Crm:ContactIndexKey</c>, or the fixed key of
    /// Development and Testing). False outside them without the key, which the application allows while <c>Features:CrmMarketing</c> is off:
    /// <see cref="IsSuppressedAsync"/>, <see cref="AddToSuppressionListAsync"/> and <see cref="StageSuppressionAsync"/> then throw
    /// <see cref="InvalidOperationException"/> (<see cref="AddByHashAsync"/> works either way: it computes nothing). A caller whose own legal
    /// record must not depend on the list (the withdrawal the host records, <c>IGdprService</c>) asks first and skips the suppression.
    /// </summary>
    bool CanIndexAddresses { get; }

    /// <summary>
    /// Checks if an email address is suppressed for the given organization: true when the list holds its keyed hash <b>or</b> the legacy
    /// plain SHA-256 of the address (an opt-out made before the index had a key keeps blocking the send).
    /// </summary>
    /// <param name="orgId">Organization ID</param>
    /// <param name="email">Email address to check (will be normalized)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the email is suppressed, false otherwise</returns>
    /// <exception cref="InvalidOperationException"><c>Crm:ContactIndexKey</c> is not configured outside Development and Testing.</exception>
    Task<bool> IsSuppressedAsync(Guid orgId, string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an email address to the suppression list (one-click unsubscribe, GDPR request, manual) with its keyed hash and, in the same
    /// <c>SaveChanges</c>, flags every CRM contact of the org with the same hash as suppressed (<c>IsSuppressed</c>, <c>SuppressedAt</c>,
    /// <c>SuppressedSource</c>). Idempotent: if the keyed hash already exists, no duplicate is created.
    /// </summary>
    /// <param name="orgId">Organization ID</param>
    /// <param name="email">Email address to suppress (will be normalized and hashed)</param>
    /// <param name="source">Source of suppression: "unsubscribe", "manual", "gdpr"</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task</returns>
    /// <exception cref="InvalidOperationException"><c>Crm:ContactIndexKey</c> is not configured outside Development and Testing.</exception>
    Task AddToSuppressionListAsync(Guid orgId, string email, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same as <see cref="AddToSuppressionListAsync"/> for a caller that already has the hash: the signed unsubscribe link carries
    /// it and never the address. The hash is stored as given (keyed for the links issued since E07/F06/US01/T02, the plain SHA-256 for the
    /// ones issued before, which stay valid 30 days) and the contacts with the same hash are flagged, which only a keyed hash can match.
    /// Idempotent. Needs no key: nothing is computed.
    /// </summary>
    /// <param name="orgId">Organization ID (the suppression list the hash goes in)</param>
    /// <param name="emailHash">The 64 hex characters of the link</param>
    /// <param name="source">Source of suppression: "unsubscribe", "manual", "gdpr"</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AddByHashAsync(Guid orgId, string emailHash, string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same as <see cref="AddToSuppressionListAsync"/> <b>without saving</b>: the entry and the flags of the contacts are added to the
    /// current unit of work (the <c>AppDbContext</c> of the scope, shared with the caller) and the caller saves them with its own changes, so
    /// a revocation and its suppression are one commit (the host's withdrawal of a marketing consent, <c>IGdprService</c>). Nothing is
    /// written until the caller saves. Returns whether a new entry was added (false: the keyed hash was already in the list).
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>Crm:ContactIndexKey</c> is not configured outside Development and Testing.</exception>
    Task<bool> StageSuppressionAsync(Guid orgId, string email, string source, CancellationToken cancellationToken = default);
}
