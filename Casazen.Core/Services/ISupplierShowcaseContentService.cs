using System.Text.Json.Nodes;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The content of a supplier's showcase as the editor reads it (SP-13a, <c>GET api/supplier/showcase/content</c>): the draft, the
/// published version, what a publication would change and when things happened.
/// </summary>
/// <param name="Version">The version the next save must carry; <c>0</c> while the supplier has never saved anything.</param>
/// <param name="State">Draft, online, changes ready to publish, or paused: derived, never stored.</param>
/// <param name="Draft">What the supplier is writing (an empty document before the first save).</param>
/// <param name="Published">What the customers read; <c>null</c> until the first publication.</param>
/// <param name="Changes">The members of the contract in which the draft differs from what is published ("cosa cambia pubblicando").</param>
/// <param name="SavedAt">The last save of the draft.</param>
/// <param name="PublishedAt">The last publication.</param>
/// <param name="PausedAt">When the supplier paused the showcase; <c>null</c> while it is not paused.</param>
/// <param name="UndoUntil">Until when the last publication can be undone; <c>null</c> when it cannot.</param>
public sealed record ShowcaseContentView(
    uint Version,
    ShowcaseContentState State,
    ShowcaseContentDocument Draft,
    ShowcaseContentDocument? Published,
    IReadOnlyList<string> Changes,
    DateTime? SavedAt,
    DateTime? PublishedAt,
    DateTime? PausedAt,
    DateTime? UndoUntil);

/// <summary>
/// The content of the supplier's showcase with its draft and its published version (SP-13a, <c>api/supplier/showcase/content</c>).
/// Every method takes the supplier org (the caller's own supplier link, never a value of the request) and reads and writes only the
/// row of that org: the table is keyed by the supplier org and not tenant-filtered, so every statement carries the explicit predicate.
/// </summary>
/// <remarks>
/// <para><b>Concurrency.</b> Every change takes the supplier's content lock and reads the row after taking it, and a save carries the
/// version the client read (<c>xmin</c>): a stale one is a 409 <see cref="ShowcaseContentErrors.VersionConflict"/>, never a lost
/// update and never a 500.</para>
/// <para><b>Plain text.</b> The texts are cleaned and the fields checked on every save (<see cref="ShowcaseContentRules"/>); a draft
/// may be incomplete.</para>
/// </remarks>
public interface ISupplierShowcaseContentService
{
    /// <summary>The content of the supplier; an empty draft (version <c>0</c>) while it never saved one. Read-only: a read creates no row.</summary>
    Task<ShowcaseContentView> GetAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a change of the draft (<paramref name="patch"/> is a JSON merge patch of the contract) when <paramref name="version"/> is the
    /// current one; the first save (version <c>0</c>) creates the row.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseContentErrors.Invalid"/>, with the fields at fault.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="ShowcaseContentErrors.VersionConflict"/>.</exception>
    Task<ShowcaseContentView> SaveDraftAsync(Guid supplierOrgId, uint version, JsonObject patch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws the changes of the draft away: it goes back to what is published (an empty draft while nothing is). When
    /// <paramref name="version"/> is given it must be the current one.
    /// </summary>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="ShowcaseContentErrors.VersionConflict"/>.</exception>
    Task<ShowcaseContentView> DiscardAsync(Guid supplierOrgId, uint? version, CancellationToken cancellationToken = default);
}
