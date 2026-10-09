namespace Casazen.Core.Services;

/// <summary>
/// The index of the e-mail addresses of the customers of the suppliers (SP-10): an HMAC-SHA256 of the normalized address keyed by
/// <c>Suppliers:CustomerIndexKey</c>. It finds the customer of an address, and counts the bookings of one address, without
/// keeping the address searchable: the key is not in the database, so a copy of the database cannot be used to test
/// addresses against it.
/// </summary>
public interface IServiceCustomerIndex
{
    /// <summary>
    /// The index of <paramref name="email"/> (trimmed, lowercase): 64 lowercase hex characters. The same address always gives the
    /// same value for the same key; a key that changes makes every earlier value unfindable (runbook: do not rotate it).
    /// </summary>
    string HashEmail(string email);
}
