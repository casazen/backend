using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Leases;

/// <summary>
/// The checks of a tenant maintenance request that do not need the database: lengths, the photo bytes, and
/// which statuses can be decided or paid.
/// </summary>
public static class LeaseMaintenanceRules
{
    public const int MaxPhotos = 5;

    /// <summary>Five megabytes of image bytes, before base64.</summary>
    public const int MaxPhotoBytes = 5 * 1024 * 1024;

    public static string CleanTitle(string? title) => Required(title, LeaseMaintenanceRequest.TitleMaxLength, "maintenance_title_invalid", "MaintenanceTitleInvalid");

    public static string CleanDescription(string? description) =>
        Required(description, LeaseMaintenanceRequest.DescriptionMaxLength, "maintenance_description_invalid", "MaintenanceDescriptionInvalid");

    public static string? CleanReason(string? reason)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length > LeaseMaintenanceRequest.ReasonMaxLength)
            throw new DomainRuleException("maintenance_reason_invalid", "MaintenanceReasonInvalid");
        return text;
    }

    public static void EnsurePayable(LeaseMaintenanceStatus status)
    {
        if (status is LeaseMaintenanceStatus.InAttesa or LeaseMaintenanceStatus.Rifiutata)
            throw new DomainConflictException("maintenance_not_payable", "MaintenanceNotPayable");
    }

    public static ImageHeaderInfo ReadPhoto(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0 || content.Length > MaxPhotoBytes)
            throw new DomainPayloadTooLargeException("maintenance_photo_too_large", "MaintenancePhotoTooLarge");
        return ImageHeaderReader.TryRead(content)
            ?? throw new DomainUnsupportedMediaTypeException("maintenance_photo_invalid", "MaintenancePhotoInvalid");
    }

    private static string Required(string? value, int max, string code, string messageKey)
    {
        var text = value?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > max)
            throw new DomainRuleException(code, messageKey);
        return text;
    }
}
