using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// The package of an accountant (E15/F01/US01/T01, NF-101, NF-87, NF-115, REP-06): one ZIP that hands an accountant a whole month or year in a fixed structure,
/// so the same files arrive every time. It composes what exists and what the other epics of the invoicing plan build (the documents of the guests, the invoices
/// of the agency to the owners, the statements, the withholding, the tourist tax, the collections) and never works a figure out a second time. Structure and
/// rules: <c>docs/TECHNICAL.md</c>, section «Accountant package».
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Always the same structure.</b> The folders of <see cref="AccountantPackageStructure"/> and their files are the same for a month and a year, for an empty
/// period and a full one: an empty list is a file with its header. <c>indice.csv</c> lists every file with its description, its rows and its SHA-256, and the
/// folders left out.</item>
/// <item><b>The service never checks roles.</b> The web layer decides what the caller may read (<see cref="AccountantPackageAccess"/>) and what properties it reaches
/// (<see cref="HostScope"/>); the owner accounting (the statements, the invoices to the owners, the withholding and the owner parts of the summary) is read
/// only with the permission and a scope that reaches the whole org, and is otherwise omitted and listed as omitted.</item>
/// <item><b>Streamed and capped.</b> The ZIP is written to the destination as it is made, the destination needing no seek and no synchronous write (the response of
/// the API has neither). The caps of the document archive apply (<c>DocumentArchive:MaxExportFiles</c>, <c>DocumentArchive:MaxExportBytes</c>): beyond them the
/// build throws 422 <c>accountant_package_too_large</c> before the first byte.</item>
/// </list>
/// </remarks>
public interface IAccountantPackageService
{
    /// <summary>
    /// Writes the package of <paramref name="period"/> to <paramref name="destination"/> (left open). Every query names the org of <paramref name="scope"/>; the
    /// documents of the guests are those of the properties the scope reaches.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">422 <c>accountant_package_too_large</c>: more files or bytes than the caps allow, before anything is written.</exception>
    Task<AccountantPackageResult> BuildAsync(
        HostScope scope,
        AccountantPackageAccess access,
        AccountantPackagePeriod period,
        Stream destination,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The agency summary of a period (E15/F01/US01/T01): the figures of the agency in one place, for the screen (<c>GET api/accountant-package/agency-summary</c>) and
/// for the ZIP (<c>riepilogo-agenzia/</c>), the same ones. Each figure comes from the service that already computes it.
/// </summary>
public interface IAgencySummaryService
{
    /// <summary>
    /// The summary of <paramref name="period"/> for the caller's reach. The owner figures (commissions, invoices to the owners, payouts, wires, the margin and the
    /// report by owner) are read only when <paramref name="access"/> allows them and <paramref name="scope"/> reaches the whole org
    /// (<see cref="AccountantPackageAccess.AllowsOwnerAccounting"/>): otherwise they are empty and <see cref="AgencySummary.OwnerAccountingIncluded"/> is false.
    /// </summary>
    Task<AgencySummary> GetAsync(
        HostScope scope,
        AccountantPackageAccess access,
        AccountantPackagePeriod period,
        CancellationToken cancellationToken = default);
}
