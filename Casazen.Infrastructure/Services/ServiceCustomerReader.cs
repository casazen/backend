using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceCustomerReader"/>
/// <remarks>
/// <para><b>Tenancy.</b> <c>ServiceCustomers</c> is keyed by the supplier org and not tenant-filtered (the TN-2 allow-list says
/// why). The statements here go through <see cref="CustomerOf"/> and <see cref="CustomersOf"/>, which carry the explicit <c>OrgId</c>
/// predicate: the customer of another supplier is not found even with a right id. <c>ShowcaseBookingTenancyTests</c> forbids any other
/// code from using the table.</para>
/// <para>The name, the address and the phone are decrypted by the column (<c>EncryptedColumns</c>); what <see cref="FindContactAsync"/>
/// returns is for the e-mails of the lifecycle of the customer's requests, never for a response of the API. The one exception is the
/// supplier's own "Clienti" page (SP-16a, <see cref="ListCardsAsync"/>): the name of one page of customers, and the e-mail and the phone only of
/// those whose job the supplier took (decision D9).</para>
/// </remarks>
public sealed class ServiceCustomerReader(AppDbContext db) : IServiceCustomerReader
{
    public async Task<IReadOnlyList<ServiceCustomerCard>> ListCardsAsync(
        Guid supplierOrgId,
        IReadOnlyCollection<Guid> customerIds,
        IReadOnlyCollection<Guid> withContact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        ArgumentNullException.ThrowIfNull(withContact);
        if (customerIds.Count == 0)
            return [];

        var ids = customerIds.ToArray();
        var contactIds = withContact.ToArray();

        // The e-mail and the phone are selected only for the customers in contactIds (a CASE in the statement, as the reader of the requests does for
        // the contact of a taken request): for the others the database returns nothing, so nothing is decrypted. An anonymized customer holds a
        // placeholder, never a person: it is told apart and its fields are not passed on.
        var rows = await CustomersOf(db, supplierOrgId, ids)
            .Select(c => new
            {
                c.Id,
                c.FullName,
                c.AnonymizedAt,
                Email = contactIds.Contains(c.Id) ? c.Email : null,
                Phone = contactIds.Contains(c.Id) ? c.Phone : null,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => row.AnonymizedAt is null
                ? new ServiceCustomerCard(row.Id, row.FullName, false, row.Email, row.Phone)
                : new ServiceCustomerCard(row.Id, string.Empty, true, null, null))
            .ToList();
    }

    /// <summary>
    /// The customers <paramref name="customerIds"/> of <paramref name="supplierOrgId"/>. Static and internal so a test can read the SQL it
    /// becomes on the PostgreSQL provider without a server.
    /// </summary>
    internal static IQueryable<ServiceCustomer> CustomersOf(AppDbContext db, Guid supplierOrgId, Guid[] customerIds) =>
        db.ServiceCustomers
            .AsNoTracking()
            .Where(c => c.OrgId == supplierOrgId && customerIds.Contains(c.Id));

    public async Task<ServiceCustomerContact?> FindContactAsync(
        Guid supplierOrgId,
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        var customer = await CustomerOf(db, supplierOrgId, customerId).FirstOrDefaultAsync(cancellationToken);
        return customer is null
            ? null
            : new ServiceCustomerContact(
                customer.Id,
                customer.FullName,
                customer.Email,
                customer.Phone,
                customer.Locale,
                customer.AnonymizedAt is not null);
    }

    /// <summary>
    /// The customer <paramref name="customerId"/> of <paramref name="supplierOrgId"/>. Static and internal so a test can read the
    /// SQL it becomes on the PostgreSQL provider without a server.
    /// </summary>
    internal static IQueryable<ServiceCustomer> CustomerOf(AppDbContext db, Guid supplierOrgId, Guid customerId) =>
        db.ServiceCustomers
            .AsNoTracking()
            .Where(c => c.OrgId == supplierOrgId && c.Id == customerId);
}
