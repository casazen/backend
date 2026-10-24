using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.ConcordatoFacile;

/// <summary>Request to manually grant a ConcordatoFacile gift to an organization with a biennial subscription.</summary>
public class AdminGrantGiftRequest
{
    /// <summary>The Stripe subscription ID that qualifies the org for the gift.</summary>
    [Required(ErrorMessage = "SubscriptionIdRequired")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>Admin's reason for manually granting the gift (max 500 chars).</summary>
    [Required(ErrorMessage = "ReasonRequired")]
    [MaxLength(500, ErrorMessage = "ReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}
