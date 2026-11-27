using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// Which leases the tenant's own area (<c>api/tenant-area</c>) may show to the person who is a tenant party of the lease. The area finds a lease
/// by the verified e-mail of the account, which is the e-mail the landlord typed for the tenant, so this rule is what keeps back what is not the
/// tenant's to read yet. It is the only definition: the list, the sheet, the rent dues, the documents, the maintenance requests and the SEPA
/// calls of the area all reach a lease through it, and a lease it hides answers "not found" like a lease of somebody else.
/// </summary>
public static class LeaseTenantAreaRules
{
    /// <summary>
    /// The leases a tenant may read: every one that is not a <see cref="LeaseStatus.Draft"/>. A draft is still the landlord's work (the wizard,
    /// a contract brought in and waiting for its signed copy, a paper contract handed out and not signed yet, the draft prepared for the
    /// ConcordatoFacile partner): it may be incomplete or wrong, and nothing in it has been agreed with the tenant. The other statuses stay
    /// readable: out for signature (<see cref="LeaseStatus.AwaitingSignature"/>, <see cref="LeaseStatus.PartiallySigned"/>), signed, in
    /// registration, registered and rejected (a contract whose registration was refused stays readable, like a concluded one).
    /// Whether the area should also hold back the leases that are out for signature is a product decision that has not been taken: today
    /// they are shown, as before.
    /// </summary>
    public static Expression<Func<LeaseContract, bool>> VisibleToTenant { get; } = lease => lease.Status != LeaseStatus.Draft;
}
