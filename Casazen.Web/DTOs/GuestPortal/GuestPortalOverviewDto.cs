using Casazen.Core.GuestPortal;

namespace Casazen.Web.DTOs.GuestPortal;

public sealed class GuestPortalOverviewDto
{
    public required GuestPortalStayDto Stay { get; init; }

    public required GuestPortalBrandingDto Branding { get; init; }

    public required string Language { get; init; }

    public required IReadOnlyList<GuestPortalSectionDto> Sections { get; init; }

    public GuestPortalNextStepDto? NextStep { get; init; }

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
                HasSyncedChannelConversation = overview.Features.HasSyncedChannelConversation,
            },
        };
    }
}

public sealed class GuestPortalStayDto
{
    public required string PropertyName { get; init; }

    public required string City { get; init; }

    public required string CheckIn { get; init; }

    public required string CheckOut { get; init; }

    public int Nights { get; init; }

    public int Guests { get; init; }

    public required string Status { get; init; }

    public required string BookingCode { get; init; }

    public bool IsOta { get; init; }

    public bool IsDirect { get; init; }
}

public sealed class GuestPortalBrandingDto
{
    public required string Name { get; init; }

    public string? LogoUrl { get; init; }

    public string? PrimaryColor { get; init; }

    public required string ThemeId { get; init; }

    public string? FontId { get; init; }

    public string? Tagline { get; init; }

    public string? Phone { get; init; }
}

public sealed class GuestPortalSectionDto
{
    public required string Key { get; init; }

    public int Order { get; init; }

    public required string State { get; init; }

    public int? Badge { get; init; }
}

public sealed class GuestPortalNextStepDto
{
    public required string Key { get; init; }

    public required string Section { get; init; }

    public string? Panel { get; init; }

    public required IReadOnlyDictionary<string, string> Params { get; init; }
}

public sealed class GuestPortalFeaturesDto
{
    public bool PaidServices { get; init; }

    public bool SupplierServices { get; init; }

    public bool AutoReplies { get; init; }

    public bool HasSyncedChannelConversation { get; init; }
}
