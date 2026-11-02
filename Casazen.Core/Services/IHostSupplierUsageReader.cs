using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// A supplier a host already asked for a job (MKT, SR-09c): what «Le mie app» of the marketplace says about it, and nothing about
/// the supplier itself (its contact is in its page, which the host opens from here).
/// </summary>
/// <param name="SupplierOrgId">The supplier org: what the page of the supplier and a new request are addressed to.</param>
/// <param name="SupplierName">The name the supplier goes by (its legal name; the name of its org when it has no profile).</param>
/// <param name="RequestsCount">Every short-rent request the host's org sent it, in the host's scope, whatever its state.</param>
/// <param name="OpenCount">Those that are not over: every one that is not refused, paid or cancelled.</param>
/// <param name="LastRequestAt">When the latest one was made (UTC).</param>
/// <param name="LastCategory">The category of the latest one (a code of <see cref="Casazen.Core.Suppliers.ServiceCategories"/>).</param>
public sealed record HostUsedSupplier(
    Guid SupplierOrgId,
    string SupplierName,
    int RequestsCount,
    int OpenCount,
    DateTime LastRequestAt,
    string LastCategory);

/// <summary>
/// The suppliers the host's org already asked for a job (MKT, SR-09c), read from its short-rent requests: a view derived from them,
/// not a relationship of its own (the trusted suppliers of the account, AM-10, are another thing).
/// </summary>
public interface IHostSupplierUsageReader
{
    /// <summary>
    /// One entry for each supplier that has at least one short-rent request of <paramref name="scope"/>'s org, the one asked most
    /// recently first. A restricted scope (a collaborator «Solo alcuni») counts only the requests of the properties it reaches, so a
    /// supplier it never asked for through them is not there. Requests of the other rental contexts and of other orgs are never counted.
    /// </summary>
    Task<IReadOnlyList<HostUsedSupplier>> ListAsync(HostScope scope, CancellationToken cancellationToken = default);
}
