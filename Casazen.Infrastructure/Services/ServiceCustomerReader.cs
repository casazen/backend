using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceCustomerReader"/>
/// <remarks>
/// <para><b>Tenancy.</b> <c>ServiceCustomers</c> is keyed by the supplier org and not tenant-filtered (the TN-2 allow-list says
/// why). The only statement here goes through <see cref="CustomerOf"/>, which carries the explicit <c>OrgId</c> predicate:
/// the customer of another supplier is not found even with a right id. <c>ShowcaseBookingTenancyTests</c> forbids any other
/// code from using the table.</para>
/// <para>The name, the address and the phone are decrypted by the column (<c>EncryptedColumns</c>); what is returned is for the
/// e-mails of the lifecycle of the customer's requests, never for a response of the API.</para>
/// </remarks>
public sealed class ServiceCustomerReader(AppDbContext db) : IServiceCustomerReader
{
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
