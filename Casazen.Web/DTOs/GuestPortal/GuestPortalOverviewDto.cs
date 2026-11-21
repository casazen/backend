using Casazen.Core.GuestPortal;

namespace Casazen.Web.DTOs.GuestPortal;

/// <summary>
/// The overview of the guest portal (<c>GET api/public/guest-portal/{token}</c>, E50/F01/US01/T02, NF-153), camelCase:
/// <c>{ stay: { propertyName, city, checkIn, checkOut, nights, guests, status, bookingCode, isOta, isDirect }, branding: { name, logoUrl, primaryColor,
/// themeId, fontId, tagline, phone }, language, sections: [{ key, order, state, badge }], nextStep: { key, section, panel, params } | null,
/// canSeeReserved, checkInCompleted, features: { paidServices, supplierServices, autoReplies } }</c>. Keys are only ever added.
/// </summary>
public sealed class GuestPortalOverviewDto
{
    public required GuestPortalStayDto Stay { get; init; }

    public required GuestPortalBrandingDto Branding { get; init; }

    /// <summary>The language of the guest: it, en, de, fr or es.</summary>
    public required string Language { get; init; }

    public required IReadOnlyList<GuestPortalSectionDto> Sections { get; init; }

    /// <summary>The block in evidence, or <c>null</c> when no section proposes one.</summary>
    public GuestPortalNextStepDto? NextStep { get; init; }

    /// <summary>
    /// Whether the sections may show the reserved data of the stay: only after the check-in, and only for a stay to come or in progress (never for a
    /// cancelled stay, a no-show or a stay already checked out).
    /// </summary>
    public bool CanSeeReserved { get; init; }

    public bool CheckInCompleted { get; init; }

    public required GuestPortalFeaturesDto Features { get; init; }

    public static GuestPortalOverviewDto From(GuestPortalOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        return new GuestPortalOverviewDto
        {
            Stay = new GuestPortalStayDto
            {
                PropertyName = overview.Stay.PropertyName,
                City = overview.Stay.City,
                CheckIn = overview.Stay.CheckIn,
                CheckOut = overview.Stay.CheckOut,
                Nights = overview.Stay.Nights,
                Guests = overview.Stay.Guests,
                Status = overview.Stay.Status,
                BookingCode = overview.Stay.BookingCode,
                IsOta = overview.Stay.IsOta,
                IsDirect = overview.Stay.IsDirect,
            },
            Branding = new GuestPortalBrandingDto
            {
                Name = overview.Branding.Name,
                LogoUrl = overview.Branding.LogoUrl,
                PrimaryColor = overview.Branding.PrimaryColor,
                ThemeId = overview.Branding.ThemeId,
                FontId = overview.Branding.FontId,
                Tagline = overview.Branding.Tagline,
                Phone = overview.Branding.Phone,
            },
            Language = overview.Language,
            Sections = overview.Sections
                .Select(s => new GuestPortalSectionDto { Key = s.Key, Order = s.Order, State = s.State, Badge = s.Badge })
                .ToList(),
            NextStep = overview.NextStep is { } step
                ? new GuestPortalNextStepDto
                {
                    Key = step.Key,
                    Section = step.Section,
                    Panel = step.Panel,
                    Params = step.Params ?? new Dictionary<string, string>(),
                }
                : null,
            CanSeeReserved = overview.CanSeeReserved,
            CheckInCompleted = overview.CheckInCompleted,
            Features = new GuestPortalFeaturesDto
            {
                PaidServices = overview.Features.PaidServices,
                SupplierServices = overview.Features.SupplierServices,
                AutoReplies = overview.Features.AutoReplies,
            },
        };
    }
}

/// <summary>The summary of the stay: never the address of the house.</summary>
public sealed class GuestPortalStayDto
{
    public required string PropertyName { get; init; }

    /// <summary>The town of the house (public, like on the booking site); the precise address is reserved.</summary>
    public required string City { get; init; }

    /// <summary>The arrival day, <c>yyyy-MM-dd</c>.</summary>
    public required string CheckIn { get; init; }

    /// <summary>The departure day, <c>yyyy-MM-dd</c>.</summary>
    public required string CheckOut { get; init; }

    public int Nights { get; init; }

    public int Guests { get; init; }

    /// <summary><c>confirmed</c>, <c>checkedIn</c>, <c>checkedOut</c> or <c>cancelled</c>.</summary>
    public required string Status { get; init; }

    /// <summary>The booking code as the guest reads it (<c>XXXXX-XXXXX</c>).</summary>
    public required string BookingCode { get; init; }

    public bool IsOta { get; init; }

    public bool IsDirect { get; init; }
}

/// <summary>The brand of the host (D3): the values of the public profile of the org.</summary>
public sealed class GuestPortalBrandingDto
{
    public required string Name { get; init; }

    public string? LogoUrl { get; init; }

    /// <summary><c>#rrggbb</c>, or <c>null</c> for the theme's own color.</summary>
    public string? PrimaryColor { get; init; }

    public required string ThemeId { get; init; }

    public string? FontId { get; init; }

    public string? Tagline { get; init; }

    /// <summary>The phone the host chose to publish on its site, or <c>null</c>.</summary>
    public string? Phone { get; init; }
}

/// <summary>An offered section: <c>state</c> is <c>available</c>, <c>locked</c>, <c>done</c> or <c>attention</c>; <c>badge</c> a count or <c>null</c>.</summary>
public sealed class GuestPortalSectionDto
{
    public required string Key { get; init; }

    public int Order { get; init; }

    public required string State { get; init; }

    public int? Badge { get; init; }
}

/// <summary>The next step: a message key of the section that proposes it, the section and panel it opens, the values the message names.</summary>
public sealed class GuestPortalNextStepDto
{
    public required string Key { get; init; }

    public required string Section { get; init; }

    public string? Panel { get; init; }

    public required IReadOnlyDictionary<string, string> Params { get; init; }
}

/// <summary>What the portal offers for the stay.</summary>
public sealed class GuestPortalFeaturesDto
{
    public bool PaidServices { get; init; }

    public bool SupplierServices { get; init; }

    public bool AutoReplies { get; init; }
}
