namespace Casazen.Core.Services;

/// <summary>
/// How to write to a private customer of a supplier (SP-10): the decrypted address and the language, read for the e-mails of the
/// request lifecycle. Never handed to a response of the API.
/// </summary>
/// <param name="Anonymized">The retention job anonymized the customer: there is nobody to write to.</param>
public sealed record ServiceCustomerContact(Guid Id, string FullName, string Email, string? Phone, string Locale, bool Anonymized);

/// <summary>
/// Reads the private customers of a supplier (SP-10) for the e-mails of the lifecycle of their requests (accepted, refused,
/// another time proposed, cancelled, expired, reminder). The table is keyed by the supplier org and not tenant-filtered, so
/// the read carries the supplier org as an explicit predicate: another supplier's customer is not found.
/// </summary>
public interface IServiceCustomerReader
{
    /// <summary>The contact of the customer <paramref name="customerId"/> of <paramref name="supplierOrgId"/>; <c>null</c> when it is not found.</summary>
    Task<ServiceCustomerContact?> FindContactAsync(Guid supplierOrgId, Guid customerId, CancellationToken cancellationToken = default);
}
