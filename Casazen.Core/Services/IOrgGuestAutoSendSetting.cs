namespace Casazen.Core.Services;

/// <summary>
/// The setting of an org that lets CasaZen write the guests of a stay to the channel manager on its own (E20/F01/US01/T01, decision O1). Off by
/// default: CHK-02 wants the data confirmed by the host, who does it in the check-in wizard. When it is on, the agency accepts that complete data plus
/// a passed identity check is that confirmation, and the guests go as soon as both are there, with no one asking. It changes nothing about who sends
/// to the police (the channel manager does, PO-02) and nothing for a property whose sender is not the channel manager.
/// </summary>
/// <remarks>
/// It sits next to the delegation of the Alloggiati transmission, on the org (<c>Org.AutoSendGuestsToOctorate</c> with who changed it and when). The
/// service never checks roles: the web layer decides who may change an org-wide setting.
/// </remarks>
public interface IOrgGuestAutoSendSetting
{
    /// <summary>Whether the org asked for it. <c>false</c> for an org that does not exist. Reads by the org given, whatever the request.</summary>
    Task<bool> GetAsync(Guid orgId, CancellationToken ct);

    /// <summary>
    /// Sets it, recording who and when. Setting the value it already has changes and records nothing. Answers whether the value changed. Throws
    /// <c>NotFoundException</c> (<c>org_not_found</c>) for an org that does not exist.
    /// </summary>
    Task<bool> SetAsync(Guid orgId, bool enabled, string userId, CancellationToken ct);
}
