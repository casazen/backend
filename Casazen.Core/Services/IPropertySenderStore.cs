using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who sends the police (Alloggiati Web) and ISTAT communications of a property (ADR-029, NF-42, NF-49, E03/F01/US05/T02): the stored
/// choice or, when there is none, the default of PO-02; and the one path that writes it. ISTAT always follows Alloggiati
/// (ADR-029-R5), so a property never has two senders.
/// </summary>
/// <remarks>
/// <para><b>The default (PO-02).</b> A property with no stored choice is sent by the channel manager while its listing is confirmed
/// (<c>OctorateListingMapping</c> with <c>Status = Confirmed</c>) through an Octorate connection of this environment that is connected;
/// otherwise it is manual. A property outside Italy (the rule of <c>TouristTaxComune.ForProperty</c>: a country that is not <c>IT</c>)
/// has no police sender to choose: manual, with <see cref="PropertySenderInfo.Applies"/> false (the Swiss files stay the host's,
/// E03/F01/US09).</para>
/// <para><b>Tenant and roles.</b> A property of another org is not found (the tenant query filter), nor a deleted one. The store never
/// checks roles: the web layer authorizes the property, and the caller (E03/F01/US02/T01) asks the confirmations and checks the
/// delegation and Octorate before <see cref="SetAsync"/>. Nothing here calls Octorate or the Questura.</para>
/// </remarks>
public interface IPropertySenderStore
{
    /// <summary>
    /// The sender of the property: the stored one, or the default; never null. Throws <c>NotFoundException</c>
    /// (<c>property_not_found</c>) for a property the caller's org does not have.
    /// </summary>
    Task<PropertySenderInfo> GetAsync(Guid propertyId, CancellationToken ct);

    /// <summary>
    /// The sender of each property, keyed by its id, read with a fixed number of queries. An id that is not found (deleted, of another
    /// org) is left out of the answer.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PropertySenderInfo>> GetManyAsync(IReadOnlyCollection<Guid> propertyIds, CancellationToken ct);

    /// <summary>
    /// The one write path. Stores <paramref name="sender"/> as the Alloggiati AND the ISTAT sender of the property, with the time and
    /// <paramref name="userId"/> (the Auth0 subject), creating the row when missing; records <c>PropertySenderChanged</c> in the activity
    /// log, in the same transaction, only when the sender really changes (from the stored one, or from the default the property had). A
    /// repeat of the stored value writes and records nothing. No confirmation, delegation or Octorate check here: the caller does them,
    /// and this line is the audit of the switch (ADR-029-R4).
    /// </summary>
    /// <remarks>
    /// Errors: <c>NotFoundException</c> <c>property_not_found</c> (not the caller's org, or deleted); <c>DomainRuleException</c>
    /// <c>sender_not_applicable</c> (outside Italy); <c>DomainConflictException</c> <c>sender_changed_concurrently</c> when another write
    /// set another sender for the property between the read and the save of this one (a concurrent first write loses to the unique index
    /// and re-reads the row): nothing of this call is written. When the concurrent write set the same sender, the call answers it.
    /// </remarks>
    Task<PropertySenderInfo> SetAsync(Guid propertyId, ComplianceSenderType sender, string userId, CancellationToken ct);
}

/// <summary>Where the sender of a property comes from.</summary>
public enum SenderSource
{
    /// <summary>A person chose it (<c>PropertyComplianceSender</c>).</summary>
    Stored = 1,

    /// <summary>No choice stored, and the listing is confirmed on a connected channel manager: the channel manager sends (PO-02).</summary>
    DefaultConnected = 2,

    /// <summary>No choice stored, and no confirmed listing on a connected channel manager: the host sends (PO-02).</summary>
    DefaultNotConnected = 3,

    /// <summary>The property is outside Italy: there is no police sender to choose.</summary>
    NotApplicable = 4,
}

/// <summary>The sender of the police and ISTAT communications of a property, as stored or by default. No personal data.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="OrgId">The org of the property.</param>
/// <param name="AlloggiatiSender">Who sends the Alloggiati Web communications.</param>
/// <param name="IstatSender">Who sends the ISTAT communications: always <paramref name="AlloggiatiSender"/>.</param>
/// <param name="Source">Stored, one of the two defaults, or not applicable.</param>
/// <param name="Applies">False for a property outside Italy (then manual).</param>
/// <param name="LastChangedAt">When a person last chose it (UTC); <c>null</c> for a default.</param>
/// <param name="LastChangedByUserId">Who chose it last (the Auth0 subject); <c>null</c> for a default.</param>
public sealed record PropertySenderInfo(
    Guid PropertyId,
    Guid OrgId,
    ComplianceSenderType AlloggiatiSender,
    ComplianceSenderType IstatSender,
    SenderSource Source,
    bool Applies,
    DateTime? LastChangedAt,
    string? LastChangedByUserId);
