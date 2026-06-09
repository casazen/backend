using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Orgs;

public class UpdateOrgPlanDto
{
    [Required]
    public string PlanTier { get; set; } = string.Empty;
}
