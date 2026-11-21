using Casazen.Core.Branding;
using Casazen.Core.Entities;

namespace Casazen.Core.GuestPortal;

/// <summary>
/// The overview of the guest portal (E50/F01/US01/T02, NF-153, AC-NF153-1, decision A4): the stay summary, the brand of the host, the language, the
/// sections with their state, the next step, what the guest may see and the features of the portal. Nothing reserved (the precise address, the
/// access instructions, the door code, the Wi-Fi) is ever part of it, whatever the check-in says: those belong to the sections that check
/// <see cref="PortalContext.CanSeeReserved"/>.
/// </summary>
public interface IGuestPortalOverviewService
{
    /// <summary>The overview of the stay of <paramref name="context"/>; <c>null</c> when the stay is no longer there (the same 404 as a bad token).</summary>
    Task<GuestPortalOverview?> GetAsync(PortalContext context, CancellationToken cancellationToken = default);
}

/// <summary>The overview of a stay. See <see cref="IGuestPortalOverviewService"/>.</summary>
public sealed record GuestPortalOverview(
    GuestPortalStay Stay,
    GuestPortalBranding Branding,
    string Language,
    IReadOnlyList<GuestPortalSectionView> Sections,
    GuestPortalStep? NextStep,
    bool CanSeeReserved,
    bool CheckInCompleted,
    GuestPortalFeatures Features);

/// <summary>
/// The summary of the stay: the name and the town of the house (never its address), the days as <c>yyyy-MM-dd</c>, the nights, the guests, the status
/// (<c>confirmed</c>, <c>checkedIn</c>, <c>checkedOut</c>, <c>cancelled</c>), the booking code as the guest reads it and the kind of booking.
/// </summary>
public sealed record GuestPortalStay(
    string PropertyName,
    string City,
    string CheckIn,
    string CheckOut,
    int Nights,
    int Guests,
    string Status,
    string BookingCode,
    bool IsOta,
    bool IsDirect);

/// <summary>
/// The brand of the host in the portal (D3): the same values as the public profile of the org (<c>GET api/public/orgs/{slug}</c>, <c>PublicOrgDto</c>):
/// the name, the logo, the color, the theme and the font of the booking site, the slogan and the phone the host chose to publish.
/// </summary>
public sealed record GuestPortalBranding(
    string Name,
    string? LogoUrl,
    string? PrimaryColor,
    string ThemeId,
    string? FontId,
    string? Tagline,
    string? Phone)
{
    /// <summary>The brand of <paramref name="org"/>, read as the public profile of the org reads it (themes and fonts resolved the same way).</summary>
    public static GuestPortalBranding From(Org org)
    {
        ArgumentNullException.ThrowIfNull(org);
        return new GuestPortalBranding(
            string.IsNullOrWhiteSpace(org.DisplayName) ? org.Name : org.DisplayName,
            org.LogoUrl,
            org.ThemeColor,
            PublicSiteThemes.Resolve(org.PublicThemeId),
            PublicSiteFonts.Resolve(org.PublicFontId),
            org.Tagline,
            org.PublicPhone);
    }
}

/// <summary>An offered section: its key, its order, the word of its state (<see cref="GuestPortalSectionStates"/>) and its badge.</summary>
public sealed record GuestPortalSectionView(string Key, int Order, string State, int? Badge);

/// <summary>
/// What the portal offers for the stay: the paid services (every <see cref="IGuestPortalPolicy"/> agrees), the services of the suppliers (the paid
/// services and <c>Features:GuestPortalSupplierServices</c>) and the automatic replies of the chat (<c>Features:GuestPortalAutoReplies</c>).
/// </summary>
public sealed record GuestPortalFeatures(bool PaidServices, bool SupplierServices, bool AutoReplies);
