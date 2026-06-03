namespace Casazen.Web.DTOs.Admin;

public class CinComplianceItemDto
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string? CinCode { get; set; }
    public string CinStatus { get; set; } = string.Empty; // "valid" | "missing" | "invalid"
    public string City { get; set; } = string.Empty;
}
