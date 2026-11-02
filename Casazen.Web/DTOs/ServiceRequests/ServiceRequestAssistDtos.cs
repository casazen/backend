using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Core.Validation;

namespace Casazen.Web.DTOs.ServiceRequests;

/// <summary>
/// Body of <c>POST api/service-requests/assist</c>: what the host wrote in «Descrivi di cosa hai bisogno» and the property the request is for.
/// Only the words reach the AI (scrubbed of e-mails, phones, fiscal codes and street addresses): the property gives the comune the suppliers are
/// looked for in, and nothing of it goes to the provider.
/// </summary>
public class ServiceRequestAssistRequest
{
    [NotEmptyGuid(ErrorMessage = "ServiceRequestPropertyRequired")]
    public Guid PropertyId { get; set; }

    /// <summary>8 to 600 characters.</summary>
    [Required(ErrorMessage = "ServiceRequestAssistTextRequired")]
    [MaxLength(ServiceRequestAssistLimits.MaxTextLength, ErrorMessage = "ServiceRequestAssistTextTooLong")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>Whether «Descrivi di cosa hai bisogno» can be offered: an AI provider is in effect (not the stub) and has a key.</summary>
public class ServiceRequestAssistAvailabilityResponse
{
    public bool Available { get; set; }
}

/// <summary>
/// What the AI made of the description of a job, or why it made nothing. Nothing is sent or saved: the wizard fills in its answers with it and the
/// host reads, changes and sends the request.
/// </summary>
public class ServiceRequestAssistResponse
{
    /// <summary>True when the AI worked out the kind of work. False: there is no suggestion and <see cref="Reason"/> says why.</summary>
    public bool AiUsed { get; set; }

    /// <summary>
    /// Why there is no suggestion when <see cref="AiUsed"/> is false: <c>ai_not_configured</c>, <c>ai_budget_exhausted</c>, <c>ai_unavailable</c>,
    /// <c>ai_invalid_answer</c> or <c>ai_not_understood</c> (the AI could not tell what the work is).
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>A code of <c>GET api/service-categories</c>.</summary>
    public string? Category { get; set; }

    /// <summary>How soon the work is needed (<c>Normal</c>, <c>High</c>, <c>Emergency</c>); the host can change it.</summary>
    public ServiceRequestUrgency? Urgency { get; set; }

    /// <summary>The work in a few words for the supplier (at most 300 characters): the wizard offers it as the notes of the request.</summary>
    public string? Summary { get; set; }

    /// <summary>One sentence for the host: why this category and this urgency (at most 160 characters).</summary>
    public string? Explanation { get; set; }

    /// <summary>
    /// <c>casazen</c> when at least one active CasaZen supplier covers the comune of the property for the category (CasaZen suppliers come
    /// first, always); <c>external</c> when none does, or the property has no comune chosen yet: the partners are proposed.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>How many CasaZen suppliers cover the comune of the property for the category (0 with <c>external</c>).</summary>
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
