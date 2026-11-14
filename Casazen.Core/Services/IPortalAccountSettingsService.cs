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
    /// <summary>The default holder and the recharge; the defaults (the owner, the recharge on) while the org has no profile.</summary>
    Task<PortalAccountSettings> GetAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Saves the default holder and the recharge; a member left out keeps its value, and an organization without a profile gets a draft one. 422
    /// <c>billing_channels_invalid</c> (with the <c>field</c>) for a holder that is not one.
    /// </summary>
    Task<PortalAccountSettings> SaveAsync(Guid orgId, PortalAccountSettingsCommand command, string? userId, CancellationToken ct = default);

    /// <summary>The holders the listings of the org have on the portals where they differ from the default, by property then portal.</summary>
    Task<IReadOnlyList<ChannelAccountHolderView>> ListAccountHoldersAsync(Guid orgId, CancellationToken ct = default);

    /// <summary>
    /// Writes the holders of the listings named, in bulk (many flats at once): a holder sets the one of the listing on the portal, <c>null</c> brings it back to
    /// the default; the listings and portals not named keep theirs. Answers the whole list as saved. 422 <c>channel_account_holders_invalid</c> (with the
    /// <c>index</c> and the <c>field</c>) for an empty list (never «all»), more than <see cref="PortalAccountErrors.MaxAccountHolders"/> rows, a property that is
    /// not one of the org, a channel that is not a portal, or a listing and portal named twice; 409 <c>channel_account_holders_conflict</c> when another save
    /// of the same listing came first. A refusal saves nothing.
    /// </summary>
    Task<IReadOnlyList<ChannelAccountHolderView>> SaveAccountHoldersAsync(
        Guid orgId,
        IReadOnlyList<ChannelAccountHolderCommand>? commands,
        string? userId,
        CancellationToken ct = default);
}

/// <summary>Who holds the accounts of the portals by default (⚖ I10) and whether their commissions are recharged to the owners (⚖ I11).</summary>
public sealed record PortalAccountSettings(PortalAccountHolder PortalAccountHolder, bool RechargePortalCommissions);

/// <summary>A save of the defaults of the portal accounts: a member left out (<c>null</c>) keeps its value.</summary>
public sealed record PortalAccountSettingsCommand
{
    public PortalAccountHolder? PortalAccountHolder { get; init; }

    public bool? RechargePortalCommissions { get; init; }
}

/// <summary>The holder of a listing on a portal, as the list answers it.</summary>
public sealed record ChannelAccountHolderView(Guid PropertyId, string PropertyName, BookingSource Channel, PortalAccountHolder Holder, DateTime UpdatedAt);

/// <summary>One row of a bulk write of the holders: the listing, the portal and the holder (<c>null</c> = back to the default of the org).</summary>
public sealed record ChannelAccountHolderCommand
{
    public Guid PropertyId { get; init; }

    /// <summary>The portal (<see cref="FiscalCopy.IsOtaBookingSource"/>).</summary>
    public BookingSource Channel { get; init; }

    public PortalAccountHolder? Holder { get; init; }
}

/// <summary>The names of the values of the portal accounts in the section <c>channels</c> of the billing setup (<c>GET api/billing-setup</c>).</summary>
public static class PortalAccountFields
{
    public const string PortalAccountHolder = "portalAccountHolder";
    public const string RechargePortalCommissions = "rechargePortalCommissions";
}

/// <summary>The stable codes of the settings of the portal accounts.</summary>
public static class PortalAccountErrors
{
    public const string ChannelsInvalid = "billing_channels_invalid";
    public const string AccountHoldersInvalid = "channel_account_holders_invalid";
    public const string AccountHoldersConflict = "channel_account_holders_conflict";

    /// <summary>The most rows one write of the holders may carry (some hundred flats on a few portals).</summary>
    public const int MaxAccountHolders = 2000;
}
