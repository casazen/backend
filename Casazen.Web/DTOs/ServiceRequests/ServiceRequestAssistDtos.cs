using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs.ServiceRequests;

public class ServiceRequestAssistRequest
{
    [NotEmptyGuid(ErrorMessage = "ServiceRequestPropertyRequired")]
    public Guid PropertyId { get; set; }

    [Required(ErrorMessage = "ServiceRequestAssistTextRequired")]
    [MaxLength(ServiceRequestAssistLimits.MaxTextLength, ErrorMessage = "ServiceRequestAssistTextTooLong")]
    public string Text { get; set; } = string.Empty;
}

public class ServiceRequestAssistAvailabilityResponse
{
    public bool Available { get; set; }
}

public class ServiceRequestAssistResponse
{
    public bool AiUsed { get; set; }

    public string? Reason { get; set; }

    public string? Category { get; set; }

    public ServiceRequestUrgency? Urgency { get; set; }

    public string? Summary { get; set; }

    public string? Explanation { get; set; }

    public string? Channel { get; set; }

    public int CasazenSuppliers { get; set; }

    public static ServiceRequestAssistResponse From(ServiceRequestAssist assist)
    {
        ArgumentNullException.ThrowIfNull(assist);

        return new ServiceRequestAssistResponse
        {
            AiUsed = assist.AiUsed,
            Reason = assist.Reason,
            Category = assist.Category,
            Urgency = assist.Urgency,
            Summary = assist.Summary,
            Explanation = assist.Explanation,
            Channel = assist.Channel,
            CasazenSuppliers = assist.CasazenSuppliers,
        };
    }
}
