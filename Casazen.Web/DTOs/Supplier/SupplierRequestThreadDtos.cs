using System.ComponentModel.DataAnnotations;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

public class SupplierRequestMessageDto
{
    public Guid Id { get; set; }

    public string Author { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }
}

public class SupplierRequestThreadDto
{
    public Guid RequestId { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool CanWrite { get; set; }

    public DateTime? RespondBy { get; set; }

    public List<SupplierRequestMessageDto> Messages { get; set; } = [];

    public string Negotiation { get; set; } = string.Empty;

    public ServiceRequestOfferDto? Offer { get; set; }

    public bool CanSendOffer { get; set; }
}

public class PostSupplierRequestMessageRequest
{
    [MaxLength(ShowcaseInquiryLimits.MessageMaxLength * 2)]
    public string? Body { get; set; }
}

public static class SupplierRequestThreadMapper
{
    public static SupplierRequestThreadDto ToDto(ServiceRequestThread thread) =>
        new()
        {
            RequestId = thread.RequestId,
            Status = thread.Status.ToString(),
            CanWrite = thread.CanWrite,
            RespondBy = thread.RespondBy is { } respondBy ? AsUtc(respondBy) : null,
            Messages = thread.Messages.Select(ToDto).ToList(),
            Negotiation = thread.Negotiation ?? ServiceRequestNegotiation.InConversation,
            Offer = thread.Offer is { } offer ? ServiceRequestOfferMapper.ToDto(offer) : null,
            CanSendOffer = thread.CanSendOffer,
        };

    public static SupplierRequestMessageDto ToDto(ServiceRequestMessageView message) =>
        new()
        {
            Id = message.Id,
            Author = message.Author.ToString(),
            Body = message.Body,
            CreatedAt = AsUtc(message.CreatedAt),
            ReadAt = message.ReadAt is { } readAt ? AsUtc(readAt) : null,
        };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
