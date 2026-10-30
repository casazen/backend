using System.Text.Json.Nodes;
using Casazen.Core.Suppliers;

namespace Casazen.Core.Services;

/// <summary>
/// The content of a supplier's showcase as the editor reads it (SP-13a, <c>GET api/supplier/showcase/content</c>): the draft, the
/// published version, what a publication would change, what is still missing and whether the bookings are open.
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
/// <param name="Readiness">What the supplier must and should have before it publishes (judged again on every publication).</param>
/// <param name="Bookings">Whether the showcase takes bookings now and, if not, why.</param>
public sealed record ShowcaseContentView(
    uint Version,
    ShowcaseContentState State,
    ShowcaseContentDocument Draft,
    ShowcaseContentDocument? Published,
    IReadOnlyList<string> Changes,
    DateTime? SavedAt,
    DateTime? PublishedAt,
    DateTime? PausedAt,
    DateTime? UndoUntil,
    ShowcaseReadinessResult Readiness,
    ShowcaseBookings Bookings);

/// <summary>What the public page reads of a published showcase: the published content, never the draft.</summary>
/// <param name="Content">The published content document.</param>
/// <param name="Paused">The supplier paused the showcase.</param>
/// <param name="PublishedAt">When it was published.</param>
public sealed record PublishedShowcase(ShowcaseContentDocument Content, bool Paused, DateTime? PublishedAt);

/// <summary>
/// Whether a supplier's showcase is published, from the row alone (a cheap read for the checklist of the console): <see cref="Exists"/> is
/// false for a supplier that never saved anything.
/// </summary>
/// <param name="Exists">The supplier has a content row.</param>
/// <param name="Published">Something is published.</param>
/// <param name="Paused">The supplier paused the showcase.</param>
/// <param name="PublishedAt">The last publication.</param>
/// <param name="SavedAt">The last save of the draft.</param>
public sealed record ShowcasePublication(bool Exists, bool Published, bool Paused, DateTime? PublishedAt, DateTime? SavedAt);

/// <summary>
/// The content of the supplier's showcase with its draft and its published version (SP-13a, <c>api/supplier/showcase/*</c>).
/// Every method takes the supplier org (the caller's own supplier link, never a value of the request) and reads and writes only the
/// row of that org: the table is keyed by the supplier org and not tenant-filtered, so every statement carries the explicit predicate.
/// </summary>
/// <remarks>
/// <para><b>Concurrency.</b> Every change takes the supplier's content lock and reads the row after taking it, and a save carries the
/// version the client read (<c>xmin</c>): a stale one is a 409 <see cref="ShowcaseContentErrors.VersionConflict"/>, never a lost
/// update and never a 500.</para>
/// <para><b>Plain text.</b> The texts are cleaned and the fields checked on every save (<see cref="ShowcaseContentRules"/>); a draft
/// may be incomplete, a publication may not.</para>
/// <para><b>The bookings.</b> <c>SupplierSettings.OnlineBookingEnabled</c> has no other writer than this service: a publication or a
/// reopening turns it on when the showcase is published, not paused, the payments are on and the supplier's account can take
/// charges and payouts; a pause, an undo of the first publication and an account that stops being ready turn it off
/// (<see cref="ShowcaseBookings"/>).</para>
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

    /// <summary>
    /// Publishes the draft: the readiness is judged again on the server, the rows left empty are dropped, the draft becomes what the
    /// customers read and the version published before is kept for <see cref="UndoPublishAsync"/> for
    /// <see cref="ShowcaseContentLimits.UndoWindow"/>. Turns the bookings on when everything they need is ready; otherwise the showcase
    /// is online in read-only mode and <see cref="ShowcaseContentView.Bookings"/> says why. When <paramref name="version"/> is given it
    /// must be the current one, so a publication never carries a draft the editor has not seen.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="ShowcaseContentErrors.SupplierNotActive"/>, <see cref="ShowcaseContentErrors.NotPublishable"/> (with the missing
    /// required items) or <see cref="ShowcaseContentErrors.Invalid"/> (a half-filled row).
    /// </exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="ShowcaseContentErrors.VersionConflict"/>.</exception>
    Task<ShowcaseContentView> PublishAsync(Guid supplierOrgId, uint? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the last publication back: what was published before (or nothing, for the first publication) is published again. The draft
    /// stays as it is. Possible only until <see cref="ShowcaseContentView.UndoUntil"/> and until something else is changed.
    /// </summary>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="ShowcaseContentErrors.UndoUnavailable"/>.</exception>
    Task<ShowcaseContentView> UndoPublishAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the published showcase: the page says it takes no bookings and the bookings close. Idempotent.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseContentErrors.NothingPublished"/>.</exception>
    Task<ShowcaseContentView> PauseAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reopens a paused showcase and, on a showcase that is not paused, judges the bookings again (the account became ready after the
    /// publication). Turns the bookings on when everything they need is ready.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException"><see cref="ShowcaseContentErrors.NotActive"/>, <see cref="ShowcaseContentErrors.NothingPublished"/>.</exception>
    Task<ShowcaseContentView> ResumeAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings <c>OnlineBookingEnabled</c> in line with the rule (published, not paused, payments on, account ready) for a supplier that
    /// has a content row: called when the supplier's Stripe account changes (<c>account.updated</c>), so the bookings close when the
    /// account stops being ready and open again when it is. A supplier with no content row is left alone. Idempotent.
    /// </summary>
    Task ReconcileBookingsAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the public page reads: the <b>published</b> content (never the draft) and whether the showcase is paused; <c>null</c> while nothing is
    /// published. Read-only, one statement scoped by the supplier org.
    /// </summary>
    Task<PublishedShowcase?> GetPublishedAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);

    /// <summary>The state of the publication from the row alone (the checklist of the console). Read-only.</summary>
    Task<ShowcasePublication> GetPublicationAsync(Guid supplierOrgId, CancellationToken cancellationToken = default);
}
