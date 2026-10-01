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

    public static LegalDocumentDto ToDto(this LegalDocumentMeta meta, LegalDocumentKind kind, LegalDocumentText? text) => new()
    {
        Key = kind.ToString().ToLowerInvariant(),
        Version = meta.Version,
        EffectiveAt = meta.EffectiveAt,
        Title = meta.Title,
        Summary = meta.Summary,
        DocumentUrl = meta.DocumentUrl,
        Available = text is not null || meta.DocumentUrl is not null,
        ContentHtml = text?.Html,
        ContentLanguage = text?.Language,
    };

    public static SubprocessorsDocumentDto ToDto(this SubprocessorsDocument doc) => new()
    {
        Version = doc.Version,
        EffectiveAt = doc.EffectiveAt,
        Items = doc.Items.Select(i => new SubprocessorItemDto
        {
            Key = i.Key,
            Name = i.Name,
            Purpose = i.Purpose,
            PurposeKey = i.PurposeKey,
            Entity = i.Entity,
            Region = i.Region,
            Website = i.Website,
            TransferMechanism = i.TransferMechanism,
            DetailsPending = i.DetailsPending,
        }).ToList(),
    };
}
