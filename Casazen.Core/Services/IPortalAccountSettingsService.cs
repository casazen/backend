using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who holds the accounts of the portals, as the billing setup sets it (E14/F01/US01/T01, ⚖ I10, I11): the values of the section <c>channels</c> of the wizard
/// «Fatturazione» (who holds the accounts by default and whether their commissions are recharged to the owners, saved by <c>PUT api/billing-setup/channels</c>)
/// and the holder of each listing and portal (<c>GET/PUT api/billing-setup/channel-account-holders</c>, in bulk). The defaults live on the profile of the issuer
/// (<see cref="OrgFiscalIssuerProfile.PortalAccountHolder"/>, <see cref="OrgFiscalIssuerProfile.RechargePortalCommissions"/>), the holders of the listings in
/// <see cref="PropertyChannelAccountHolder"/>; <see cref="IPortalAccountHolderResolver"/> is the one reader of the holder for the other epics.
/// </summary>
/// <remarks>The service names the org of everything it reads and writes; the web layer decides who may call it (the whole organization).</remarks>
public interface IPortalAccountSettingsService
{
    Task<PortalAccountSettings> GetAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Saves the default holder and the recharge; a member left out keeps its value, and an organization without a profile gets a draft one. 422
    /// <c>billing_channels_invalid</c> (with the <c>field</c>) for a holder that is not one. A save that starts the waiver of the recharge (after it the recharge is off and the
    /// agency holds at least one account, its default or the one of a listing; before it the recharge was on, or the agency held none) is a renunciation of the reimbursement
    /// the law gives the agency (decision 1 of 20/11/2026: the commissions of those accounts become a cost of the agency): it needs
    /// <see cref="PortalAccountSettingsCommand.AcknowledgeRechargeWaiver"/>, else 422 <see cref="PortalAccountErrors.RechargeWaiverNotAcknowledged"/> and nothing is saved; with it
    /// the activity log of the organization records who acknowledged it and when. It is not retroactive: the posting of the months still open follows it, the published
    /// versions and the closed months never change.
    /// </summary>
    Task<PortalAccountSettings> SaveAsync(Guid orgId, PortalAccountSettingsCommand command, string? userId, CancellationToken ct = default);

    Task<IReadOnlyList<ChannelAccountHolderView>> ListAccountHoldersAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Writes the holders of the listings named, in bulk (many flats at once): a holder sets the one of the listing on the portal, <c>null</c> brings it back to
    /// the default; the listings and portals not named keep theirs. Answers the whole list as saved. 422 <c>channel_account_holders_invalid</c> (with the
    /// <c>index</c> and the <c>field</c>) for an empty list (never «all»), more than <see cref="PortalAccountErrors.MaxAccountHolders"/> rows, a property that is
    /// not one of the org, a channel that is not a portal, or a listing and portal named twice; 409 <c>channel_account_holders_conflict</c> when another save
    /// of the same listing came first. A refusal saves nothing. A write that starts the waiver of the recharge (the recharge off and, after it, a listing of the agency where
    /// the agency held no account before: the same effect as a <see cref="SaveAsync"/> that makes the agency the default holder) needs
    /// <paramref name="acknowledgeRechargeWaiver"/> (<c>?acknowledgeRechargeWaiver=true</c>), else 422 <see cref="PortalAccountErrors.RechargeWaiverNotAcknowledged"/> and
    /// nothing is written; with it the activity log of the organization records who acknowledged it and when, as <see cref="SaveAsync"/> does.
    /// </summary>
    Task<IReadOnlyList<ChannelAccountHolderView>> SaveAccountHoldersAsync(
        Guid orgId,
        IReadOnlyList<ChannelAccountHolderCommand>? commands,
        string? userId,
        bool acknowledgeRechargeWaiver = false,
        CancellationToken ct = default);
}

public sealed record PortalAccountSettings(PortalAccountHolder PortalAccountHolder, bool RechargePortalCommissions);

public sealed record PortalAccountSettingsCommand
{
    public PortalAccountHolder? PortalAccountHolder { get; init; }

    public bool? RechargePortalCommissions { get; init; }

    /// <summary>
    /// The explicit acknowledgement of the waiver of the recharge (<c>acknowledgeRechargeWaiver: true</c>): the person confirms that the agency renounces recharging to the
    /// owners the commissions of the portal accounts it holds, which it then bears itself. Needed only by a save that starts the waiver
    /// (<see cref="IPortalAccountSettingsService.SaveAsync"/>; the bulk write of the holders takes it as the query parameter of the same name); ignored otherwise.
    /// </summary>
    public bool? AcknowledgeRechargeWaiver { get; init; }
}

public sealed record ChannelAccountHolderView(Guid PropertyId, string PropertyName, BookingSource Channel, PortalAccountHolder Holder, DateTime UpdatedAt);

public sealed record ChannelAccountHolderCommand
{
    public Guid PropertyId { get; init; }

    public BookingSource Channel { get; init; }

    public PortalAccountHolder? Holder { get; init; }
}

public static class PortalAccountFields
{
    public const string PortalAccountHolder = "portalAccountHolder";
    public const string RechargePortalCommissions = "rechargePortalCommissions";
}

public static class PortalAccountErrors
{
    public const string ChannelsInvalid = "billing_channels_invalid";
    public const string AccountHoldersInvalid = "channel_account_holders_invalid";
    public const string AccountHoldersConflict = "channel_account_holders_conflict";

    public const string RechargeWaiverNotAcknowledged = "recharge_waiver_not_acknowledged";

    public const string RechargeWaiverActivityPart = "portalCommissionRechargeWaiver";

    public const int MaxAccountHolders = 2000;
}
