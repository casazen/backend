using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The hand-off of a Partner Hub partner (E09/F02/US03/T01, NF-56, MKT-03, S18, D16, Q-006): the brief the host takes to the partner's own site, and the
/// record that the host activated the partner there. A partner is activated on its own site, so CasaZen calls nobody: it prepares what the partner will
/// ask, and remembers what the host says it did. The status is the host's word (<c>IsDeclared</c>, with the day it gave) and becomes verified only where
/// an Octorate connection shows it (Q-006).
/// </summary>
/// <remarks>
/// <para><b>Org and property.</b> A declaration without a property is the whole org's (an org-level row of <c>MarketplaceActivation</c>); with a property it
/// is that property's. Every method takes the caller's <see cref="HostScope"/>: the scope of another org is an <see cref="ArgumentException"/> (fail
/// closed); a property outside the scope is the 404 of a property that does not exist (<c>marketplace_property_not_found</c>); an org-level declaration
/// is an org-level decision, so a caller limited to some properties does not make it (403 <see cref="MarketplacePartnerErrorCodes.NeedsWholeOrg"/>):
/// «no property named» is never «inside the scope».</para>
/// <para><b>Who may see a partner.</b> The partner must be visible to the org (<see cref="MarketplaceVisibility"/>: the admin made it visible, and the plan
/// and the billing country of the org allow it); a hidden partner, one the plan or the country does not allow and a code nobody has are the same 404
/// (<c>marketplace_entry_not_found</c>).</para>
/// <para><b>Nothing leaves.</b> The brief holds the partner's name and site, the checklist of what to choose and authorise, and a fixed list of the
/// property's fields; it never holds a guest, a booking, a price or a document. Nothing is sent to the partner and nothing is asked of Octorate.</para>
/// </remarks>
public interface IMarketplacePartnerActivationService
{
    /// <summary>
    /// The brief of the partner for the host: its name and site, the checklist of what to choose and what to authorise on the partner's site (the lines
    /// of the activation guide in <paramref name="language"/>, Italian when the partner has no text in it), and, when <paramref name="propertyId"/> is
    /// given, the property's data the partner is likely to ask for. Reads only; nothing is recorded.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">
    /// <c>marketplace_property_not_found</c> (the property is not the org's or is outside the scope), <c>marketplace_entry_not_found</c> (the partner is
    /// unknown, hidden, or not for the org's plan or country).
    /// </exception>
    Task<MarketplacePartnerBrief> GetBriefAsync(
        Guid orgId,
        HostScope scope,
        string partnerCode,
        Guid? propertyId,
        string language = MarketplaceLanguages.Italian,
        CancellationToken ct = default);

    /// <summary>
    /// Records that the host activated the partner on its own site, on <paramref name="declaredOn"/>: the row (kind <c>Partner</c>, target = the partner's
    /// code, the property given or none) becomes Active and declared, with that day as <c>DeclaredAt</c> (midnight UTC of the day, the storage convention
    /// of date-only values). Idempotent: a row that is already Active is returned as it is, whatever the day given, and its declaration (the day included) is
    /// never rewritten; a row that was turned off, or is to fix, is made Active again. When the partner has a portal code in Octorate's catalog and the
    /// property has an Active, proven connection of that portal (a channel or a tool), the row is verified instead (<c>VerifiedAt</c> set, no longer
    /// declared); an org-level declaration has no property to compare and stays declared. The proof is looked at again for a row that is Active and only
    /// declared (the connection came after the declaration): then, and only then, a declaring again writes: <c>VerifiedAt</c> and <c>UpdatedAt</c> are set
    /// and <c>IsDeclared</c> is cleared, the rest of the row stays. Nothing is asked of Octorate: only CasaZen's own rows are read, and only while the
    /// connector is on and the org is connected. <paramref name="userId"/> is the person who declared, for the log.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException"><c>marketplace_declared_date_invalid</c>: the day is after today in Rome.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException"><c>marketplace_partner_needs_whole_org</c>: no property, and the caller does not reach the whole org.</exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_property_not_found</c>, <c>marketplace_entry_not_found</c>.</exception>
    Task<MarketplacePartnerActivation> DeclareAsync(
        Guid orgId,
        HostScope scope,
        string partnerCode,
        Guid? propertyId,
        DateOnly declaredOn,
        string userId,
        CancellationToken ct = default);

    /// <summary>
    /// Records that the host turned the partner off on its own site (the guided deactivation of the change service): the row becomes
    /// <see cref="MarketplaceActivationStatus.Deactivated"/> and is kept as history. Idempotent: a row that is already Deactivated is returned as it is.
    /// The partner need not be visible any more: a host can always turn off what it activated. <paramref name="userId"/> is the person who declared, for the log.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException"><c>marketplace_activation_not_found</c> (the org has no such row), <c>marketplace_property_not_found</c>.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainForbiddenException"><c>marketplace_partner_needs_whole_org</c>.</exception>
    Task<MarketplacePartnerActivation> UndeclareAsync(
        Guid orgId,
        HostScope scope,
        string partnerCode,
        Guid? propertyId,
        string userId,
        CancellationToken ct = default);
}

/// <summary>The stable codes of the partner hand-off (E09/F02/US03/T01): the answers of the declaration.</summary>
public static class MarketplacePartnerErrorCodes
{
    /// <summary>422: the day of the declaration is missing or after today (Europe/Rome).</summary>
    public const string DeclaredDateInvalid = "marketplace_declared_date_invalid";

    /// <summary>403: a declaration for the whole org is an org-level decision, and the caller is limited to some properties.</summary>
    public const string NeedsWholeOrg = "marketplace_partner_needs_whole_org";
}

/// <summary>
/// What the host takes to the partner's site (<c>GET api/marketplace/partners/{code}/brief</c>). <see cref="Checklist"/> is what to choose and what to
/// authorise there, one item per line of the activation guide; <see cref="PropertyData"/> is <c>null</c> when no property was asked about. It holds no guest,
/// no booking, no price and no document.
/// </summary>
/// <param name="Code">The partner's code in the catalog.</param>
/// <param name="Name">The partner's name.</param>
/// <param name="WebsiteUrl">The partner's site (https), where the host activates it.</param>
/// <param name="Checklist">What to choose and what to authorise on the partner's site, in the language asked (Italian when the partner has no text in it).</param>
/// <param name="PropertyData">The fixed list of the property's fields; <c>null</c> without a property.</param>
public sealed record MarketplacePartnerBrief(
    string Code,
    string Name,
    string WebsiteUrl,
    IReadOnlyList<string> Checklist,
    MarketplacePartnerPropertyData? PropertyData);

/// <summary>
/// The explicit whitelist of a property's fields the brief may carry (privacy rule): name, address, city, postal code, rooms and capacity, and the CIN only
/// when it is valid (<c>CinFormat</c>; an invalid or missing CIN is <c>null</c>). Nothing else of the property, and nothing about guests, bookings, prices or
/// documents, is ever added to it.
/// </summary>
/// <param name="Name">The property's name.</param>
/// <param name="Address">The street address.</param>
/// <param name="City">The city.</param>
/// <param name="PostalCode">The postal code.</param>
/// <param name="Bedrooms">The number of bedrooms.</param>
/// <param name="Bathrooms">The number of bathrooms.</param>
/// <param name="MaxGuests">The maximum number of guests.</param>
/// <param name="CinCode">The normalized CIN, only when its status is valid; otherwise <c>null</c>.</param>
public sealed record MarketplacePartnerPropertyData(
    string Name,
    string Address,
    string City,
    string PostalCode,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    string? CinCode);

/// <summary>
/// The activation of a partner as the declaration leaves it. <see cref="IsDeclared"/> is true while only the host's word backs it; a connection that shows
/// the partner active makes it verified (<see cref="VerifiedAt"/> set, <see cref="IsDeclared"/> false).
/// </summary>
/// <param name="ActivationId">The activation row.</param>
/// <param name="PartnerCode">The partner's code.</param>
/// <param name="PropertyId">The property it was declared for; <c>null</c> for the whole org.</param>
/// <param name="Status">Where it stands: Active after a declaration, Deactivated after the guided deactivation.</param>
/// <param name="IsDeclared">The status was declared by the host and no connection proved it.</param>
/// <param name="DeclaredAt">The day the host gave, as midnight UTC of that day.</param>
/// <param name="VerifiedAt">When a connection first showed it (UTC); <c>null</c> while it is only declared.</param>
public sealed record MarketplacePartnerActivation(
    Guid ActivationId,
    string PartnerCode,
    Guid? PropertyId,
    MarketplaceActivationStatus Status,
    bool IsDeclared,
    DateTime? DeclaredAt,
    DateTime? VerifiedAt);
