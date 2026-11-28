using Casazen.Core.Branding;
using Casazen.Core.Entities;

namespace Casazen.Core.GuestPortal;

public interface IGuestPortalOverviewService
{
    Task<GuestPortalOverview?> GetAsync(PortalContext context, CancellationToken cancellationToken = default);
}

public sealed record GuestPortalOverview(
    GuestPortalStay Stay,
    GuestPortalBranding Branding,
    string Language,
    IReadOnlyList<GuestPortalSectionView> Sections,
    GuestPortalStep? NextStep,
    bool CanSeeReserved,
    bool CheckInCompleted,
    GuestPortalFeatures Features);

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

public sealed record GuestPortalBranding(
    string Name,
    string? LogoUrl,
    string? PrimaryColor,
    string ThemeId,
    string? FontId,
    string? Tagline,
    string? Phone)
{
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

public sealed record GuestPortalSectionView(string Key, int Order, string State, int? Badge);

public sealed record GuestPortalFeatures(bool PaidServices, bool SupplierServices, bool AutoReplies, bool HasSyncedChannelConversation = false);
