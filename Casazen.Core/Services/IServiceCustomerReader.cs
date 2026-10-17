namespace Casazen.Core.Services;

/// <summary>
/// How to write to a private customer of a supplier (SP-10): the decrypted address and the language, read for the e-mails of the
/// request lifecycle. Never handed to a response of the API.
/// </summary>
/// <param name="Anonymized">The retention job anonymized the customer: there is nobody to write to.</param>
public sealed record ServiceCustomerContact(Guid Id, string FullName, string Email, string? Phone, string Locale, bool Anonymized);

/// <summary>
/// A private customer as the supplier's "Clienti" page needs it (SP-16a): the name, and the e-mail and the phone <b>only when the supplier took a job of
/// that customer</b> (decision D9). Both are null for a customer the retention job removed.
/// </summary>
/// <param name="FullName">The full name as the customer wrote it; the page shortens it to "Nome C." until a job is taken.</param>
/// <param name="Anonymized">The retention job anonymized the customer: there is nobody left to name or reach.</param>
/// <param name="Email">The e-mail address; null when not asked for, or when the customer is anonymized.</param>
/// <param name="Phone">The phone number; null when not asked for, when the customer left none, or when it is anonymized.</param>
public sealed record ServiceCustomerCard(Guid Id, string FullName, bool Anonymized, string? Email, string? Phone);

/// <summary>
/// Reads the private customers of a supplier (SP-10) for the e-mails of the lifecycle of their requests (accepted, refused,
/// another time proposed, cancelled, expired, reminder) and, since SP-16a, for the supplier's own "Clienti" page. The table is keyed by the
/// supplier org and not tenant-filtered, so the read carries the supplier org as an explicit predicate: another supplier's customer is not found.
/// </summary>
public interface IServiceCustomerReader
{
    /// <summary>The contact of the customer <paramref name="customerId"/> of <paramref name="supplierOrgId"/>; <c>null</c> when it is not found.</summary>
    Task<ServiceCustomerContact?> FindContactAsync(Guid supplierOrgId, Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The customers <paramref name="customerIds"/> of <paramref name="supplierOrgId"/> (SP-16a): another supplier's customer is not in the answer.
    /// The e-mail and the phone are read <b>only</b> for the ids in <paramref name="withContact"/> (the customers whose job the supplier took): for the
    /// others the database is not even asked for them, so they are never decrypted. For one page of the clients list, never for the whole table.
    /// </summary>
    Task<IReadOnlyList<ServiceCustomerCard>> ListCardsAsync(
        Guid supplierOrgId,
        IReadOnlyCollection<Guid> customerIds,
        IReadOnlyCollection<Guid> withContact,
        CancellationToken cancellationToken = default);
}
