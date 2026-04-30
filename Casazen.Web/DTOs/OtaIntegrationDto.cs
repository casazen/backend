namespace Casazen.Web.DTOs;

public class OtaIntegrationDto
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string ExternalPropertyId { get; set; } = string.Empty;
    public string ApiKeyMasked { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateOtaIntegrationRequest
{
    public Guid PropertyId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string ExternalPropertyId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public class UpdateOtaIntegrationRequest
{
    public string? ExternalPropertyId { get; set; }
    public string? ApiKey { get; set; }
    public bool? IsActive { get; set; }
}
