namespace Casazen.Core.Services;

/// <summary>
/// The keyed index of the delegated contacts of an owner (E07/F07/US01/T02, AC-NF111-4): an HMAC-SHA256 of the normalized address of a contact keyed by
/// <c>OwnerPortal:ContactIndexKey</c>. It finds a contact and keeps one contact in force per owner profile and address (<c>OwnerPortalContact.EmailHash</c>) while
/// the address itself is encrypted at rest: the key is not in the database, so a copy of the database cannot be used to test addresses against it, and the
/// encryption is never deterministic. The pattern of <see cref="IServiceCustomerIndex"/> and <see cref="ICrmContactIndex"/>.
/// </summary>
public interface IOwnerContactIndex
{
    /// <summary>
    /// True when <see cref="HashEmail"/> can be computed: <c>OwnerPortal:ContactIndexKey</c> is configured, or the fixed key of Development and Testing stands
    /// in. False outside them without the key, which the application allows while <c>Features:AgencyTier</c> is off (the startup asks for the key only with the
    /// flag on).
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// The index of an address: 64 lowercase hex characters, the same for the same address (trimmed, lowercase) and key. A key that changes makes every earlier
    /// address unfindable (runbook: do not rotate it).
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is not configured outside Development and Testing: never an HMAC with an empty key.</exception>
    string HashEmail(string email);
}
