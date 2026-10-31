using System.ComponentModel.DataAnnotations;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>One message of the thread of a request on measure, as the supplier reads it (SP-10b).</summary>
public class SupplierRequestMessageDto
{
    public Guid Id { get; set; }

    /// <summary><c>Customer</c>, <c>Supplier</c> or <c>System</c>.</summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>What was written: plain text. Personal data, shown to the two parties of the request only.</summary>
    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the <b>other</b> party first read it; <c>null</c> while it did not. For a message of the customer, as the answer was before this
    /// reading: <c>null</c> marks the ones that are new to the supplier.
    /// </summary>
    public DateTime? ReadAt { get; set; }
}

/// <summary>
/// Answer of <c>GET api/supplier/requests/{id}/thread</c> (SP-10b): the conversation with the customer of a request on measure. Until the supplier
/// takes the request the console names the customer "Nome C." (the request's <c>clientName</c>); nothing of the customer's contacts is here.
/// </summary>
public class SupplierRequestThreadDto
{
    public Guid RequestId { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>The request is open and the supplier is active: a message can be added.</summary>
    public bool CanWrite { get; set; }

    /// <summary>The instant the supplier has to answer by, while nobody answered; <c>null</c> after its first message.</summary>
    public DateTime? RespondBy { get; set; }

    /// <summary>In the order they were written; at most 200.</summary>
    public List<SupplierRequestMessageDto> Messages { get; set; } = [];

    /// <summary>
    /// SP-10b-b: where the negotiation is — <c>AwaitingSupplier</c>, <c>InConversation</c>, <c>OfferSent</c> (an offer waits for the customer),
    /// <c>OfferDeclined</c> (the customer turned the last one down), <c>Agreed</c>, <c>Declined</c>, <c>Expired</c> (the customer did not answer the
    /// offer in time) or <c>Closed</c>. An open set: treat an unknown state as <c>InConversation</c>.
    /// </summary>
    public string Negotiation { get; set; } = string.Empty;

    /// <summary>SP-10b-b: the offer that waits for the customer's answer; <c>null</c> when there is none.</summary>
    public ServiceRequestOfferDto? Offer { get; set; }

    /// <summary>SP-10b-b: the request is new and the supplier is active: <c>POST api/supplier/requests/{id}/offer</c> is possible (a new offer replaces the one that waits).</summary>
    public bool CanSendOffer { get; set; }
}

/// <summary>Body of <c>POST api/supplier/requests/{id}/messages</c>.</summary>
public class PostSupplierRequestMessageRequest
{
    /// <summary>The message: 1 to 1000 characters of plain text. The attribute only stops an oversized body; the rule is the service's (422).</summary>
    [MaxLength(ShowcaseInquiryLimits.MessageMaxLength * 2)]
    public string? Body { get; set; }
}

/// <summary>Maps the thread to the API DTOs.</summary>
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
