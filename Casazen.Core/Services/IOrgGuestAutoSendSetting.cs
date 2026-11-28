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
    Task<bool> GetAsync(Guid orgId, CancellationToken ct);

    Task<bool> SetAsync(Guid orgId, bool enabled, string userId, CancellationToken ct);
}
