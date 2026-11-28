using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.ConcordatoFacile;

public class AdminGrantGiftRequest
{
    [Required(ErrorMessage = "SubscriptionIdRequired")]
    public string SubscriptionId { get; set; } = string.Empty;

    [Required(ErrorMessage = "ReasonRequired")]
    [MaxLength(500, ErrorMessage = "ReasonTooLong")]
    public string Reason { get; set; } = string.Empty;
}
