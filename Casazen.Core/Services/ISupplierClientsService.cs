using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The customers of a supplier (SP-16a, <c>GET api/supplier/clients</c>): the hosts of CasaZen and the private customers of the public showcase, with
/// what they did together. There is no table of customers for the hosts: a customer is made by aggregating the supplier's requests by the customer id
/// they carry. Read-only, and every read is scoped by the supplier org of the caller (the requests and the private customers have two parties or are
/// keyed by the supplier, with no tenant filter, so the org is an explicit predicate: another supplier's customer is never read).
/// </summary>
/// <remarks>
/// <para><b>Privacy.</b> The name of a private customer is "Nome C." until a job of theirs is taken, then the full name; their e-mail and phone come only
/// then (decision D9, the same line as the inbox). The personal columns are encrypted and are read <b>only for the page</b> returned, and the e-mail and
/// the phone only of the customers whose job was taken. A customer the retention job removed has no name and no contact. Nothing here is exported
/// (decision SP-D25) and nobody can search by name in the database (decision SP-D23: the console searches the rows it loaded).</para>
/// </remarks>
public interface ISupplierClientsService
{
    /// <summary>A page of the customers, the one with the latest activity first, and how many there are for <paramref name="filter"/>.</summary>
    Task<SupplierClientsPage> ListAsync(
        Guid supplierOrgId,
        SupplierClientFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>One customer with its latest requests.</summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="SupplierClientErrors.NotFound"/>: not a customer of this supplier.</exception>
    Task<SupplierClientDetail> GetAsync(Guid supplierOrgId, Guid clientId, CancellationToken cancellationToken = default);
}
