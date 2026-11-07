namespace Casazen.Core.Options;

/// <summary>Meta WhatsApp Cloud API settings. Secrets are supplied by the environment and never persisted.</summary>
public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";
    public bool Enabled { get; set; }
    public string ApiVersion { get; set; } = "v22.0";
    public string PhoneNumberId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RentReminderTemplate { get; set; } = "rent_reminder";
    public string LanguageCode { get; set; } = "it";
}
