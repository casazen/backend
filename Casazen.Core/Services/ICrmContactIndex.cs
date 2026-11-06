namespace Casazen.Core.Services;

/// <summary>
/// The keyed index of the CRM (E07/F06/US01/T01, NF-95, NF-89): an HMAC-SHA256 of a normalized contact value (an e-mail address, a phone
/// number) keyed by <c>Crm:ContactIndexKey</c>. It finds a contact (<c>GuestContact.ValueHash</c>) and a suppressed address
/// (<c>SuppressionListEntry.EmailHash</c>) without keeping the value searchable: the key is not in the database, so a copy of the database
/// cannot be used to test addresses against it. The pattern of <see cref="IServiceCustomerIndex"/>.
/// </summary>
public interface ICrmContactIndex
{
    /// <summary>
    /// The index of <paramref name="normalizedValue"/>: 64 lowercase hex characters, the same for the same value and key. <b>The value is
    /// normalized by the caller</b> (an address with <c>ShowcaseBookingRules.NormalizeEmail</c>, a phone number with the CRM normalizer): the
    /// index is only as stable as the form it is given. A key that changes makes every earlier value unfindable (runbook: do not rotate it).
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is not configured outside Development and Testing: never an HMAC with an empty key.</exception>
    string Hash(string normalizedValue);
}
