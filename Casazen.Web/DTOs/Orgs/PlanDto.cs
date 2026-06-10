namespace Casazen.Web.DTOs.Orgs;

public class PlanDto
{
    public string Tier { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int MaxProperties { get; set; }
    public string Description { get; set; } = string.Empty;
}
