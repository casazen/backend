namespace Casazen.Core.Services;

/// <summary>
/// Creates a property on Octorate (E02/F03/US01/T06, section 9.1 of the requirements, PO-19, ADR-023, ADR-027-R8): the <c>newInventory</c> entry of
/// the connect flow. A property a person explicitly declared never published, which CasaZen has and Octorate does not, is created on the channel
/// manager, the token of the new accommodation is saved without any consent, and the accommodation becomes a Confirmed mapping of the property.
/// </summary>
/// <remarks>
/// <para><b>Never for existing inventory.</b> It refuses a property declared <c>onOctorate</c> or <c>onOtas</c> (or declared nothing) before any
/// call: the absence of a confirmed mapping is not proof that the inventory is new, and an import never turns into a creation.</para>
/// <para>There is no endpoint of its own: the publication (<c>IContentPushService</c>) calls it when the property has no confirmed mapping and its
/// stored scenario is <c>newInventory</c>. Everything runs under <see cref="IOctorateOrgScope.Use"/> for the org of the property.</para>
/// </remarks>
public interface IOctoratePropertyCreator
{
    /// <summary>
    /// Creates the property on Octorate and returns the id of the new accommodation. The guards run before any call: the property is of an org and
    /// within the reach of <paramref name="userId"/> (404 <c>octorate_property_not_found</c>), its scenario is <c>newInventory</c> (409
    /// <c>octorate_existing_inventory_must_import</c>), it has no confirmed mapping (409 <c>octorate_property_exists</c>) and sits in no proposed or
    /// ambiguous row (409 <c>octorate_mapping_unconfirmed</c>), and its data are complete (422 <c>octorate_property_invalid</c> with the fields).
    /// The refresh token Octorate gives once is saved first, before anything else can fail.
    /// </summary>
    /// <param name="propertyId">The property to create.</param>
    /// <param name="userId">The person who publishes: their reach is checked, and their name is the referent of the new accommodation.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="tokenRoles">
    /// The roles of the token of <paramref name="userId"/>, which decide the reach of a person in no org team exactly as the web layer decided it;
    /// <c>null</c> = none (such a person reaches only the properties it created).
    /// </param>
    Task<string> CreateAsync(Guid propertyId, string userId, CancellationToken ct, IReadOnlySet<string>? tokenRoles = null);
}
