using Casazen.Core.Models;
using Casazen.Web.DTOs.Legal;
using Casazen.Web.DTOs.Onboarding;

namespace Casazen.Web.Mapping;

public static class OnboardingMapping
{
    public static OnboardingConsentsInput? ToInput(this OnboardingConsentsDto? dto) =>
        dto is null
            ? null
            : new OnboardingConsentsInput(
                dto.TosAccepted,
                dto.TosVersion,
                dto.PrivacyAccepted,
                dto.PrivacyVersion,
                dto.DpaAccepted,
                dto.DpaVersion,
                dto.SubprocessorsAcknowledged,
                dto.SubprocessorsVersion,
                dto.MarketingOptIn);

    public static OnboardingStatusDto ToDto(this OnboardingActivationStatus status) => new()
    {
        RoleChosen = status.RoleChosen,
        OrgProvisioned = status.OrgProvisioned,
        ConsentsAccepted = status.ConsentsAccepted,
        PropertyCreated = status.PropertyCreated,
        SitePublished = status.SitePublished,
        FirstBookingTaken = status.FirstBookingTaken,
        Activated = status.Activated,
        PublicBookingUrl = status.PublicBookingUrl,
    };

    public static LegalDocumentDto ToDto(this LegalDocumentMeta meta) => new()
    {
        Version = meta.Version,
        EffectiveAt = meta.EffectiveAt,
        Title = meta.Title,
        Summary = meta.Summary,
        DocumentUrl = meta.DocumentUrl,
    };

    public static SubprocessorsDocumentDto ToDto(this SubprocessorsDocument doc) => new()
    {
        Version = doc.Version,
        EffectiveAt = doc.EffectiveAt,
        Items = doc.Items.Select(i => new SubprocessorItemDto
        {
            Name = i.Name,
            Purpose = i.Purpose,
            Region = i.Region,
            Website = i.Website,
        }).ToList(),
    };
}
