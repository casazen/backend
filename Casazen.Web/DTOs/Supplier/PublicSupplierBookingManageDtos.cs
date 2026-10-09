using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

// The customer's own area of a booking made from a supplier's public showcase (SP-11), written by a customer with no account who
// proves it is the one who booked with the code of the booking and its e-mail address. The code and the address are in the BODY,
// never in the URL, so neither is in an access log. The answers carry the booking and what the customer can do with it: never the
// data of another customer, never what the supplier noted for itself, and — before the supplier took the request — never the exact
// address (ShowcaseBookingView).

/// <summary>
/// Body of <c>POST api/public/supplier-bookings/lookup</c>, <c>…/proposal/accept</c> and <c>…/proposal/reject</c>, and the start of
/// every other call of the area: who the customer says it is. The attributes only refuse what is missing or oversized (400
/// <c>validation_error</c>, which tells nothing about any booking); a slug, a code or an address that does not match is the same 404
/// as a booking that does not exist.
/// </summary>
public class SupplierBookingAccessRequest : IPerEmailRateLimitedRequest
{
    /// <summary>The slug of the supplier's showcase (<c>/fornitori/{slug}</c>), as in the link of the e-mail.</summary>
    [Required(ErrorMessage = "SupplierBookingAccessRequired")]
    [MaxLength(PublicShowcaseLimits.SlugMaxLength + 50, ErrorMessage = "SupplierBookingAccessRequired")]
    public string? Slug { get; set; }

    /// <summary>The code of the booking, <c>XXXXX-XXXXX</c>, from the e-mails (separators and case are ignored).</summary>
    [Required(ErrorMessage = "SupplierBookingAccessRequired")]
    [MaxLength(64, ErrorMessage = "SupplierBookingAccessRequired")]
    public string? Code { get; set; }

    /// <summary>The e-mail address the booking was made with.</summary>
    [Required(ErrorMessage = "SupplierBookingAccessRequired")]
    [MaxLength(ShowcaseBookingLimits.EmailMaxLength + 50, ErrorMessage = "SupplierBookingAccessRequired")]
    public string? Email { get; set; }

    /// <summary>The per-address limit counts the address within the supplier (the same address at two suppliers has two budgets).</summary>
    string? IPerEmailRateLimitedRequest.RateLimitScope => Slug;

    public ShowcaseBookingCredentials ToCredentials() => new(Slug, Code, Email);
}

/// <summary>Body of <c>POST api/public/supplier-bookings/cancel</c>: who the customer is, and why (optional).</summary>
public class SupplierBookingCancelRequest : SupplierBookingAccessRequest
{
    /// <summary>Why the customer cancels, at most 500 characters (optional); a longer one is 422 <c>supplier_booking_invalid</c> naming <c>reason</c>.</summary>
    [MaxLength(ServiceRequestLimits.CancellationReasonMaxLength + 100)]
    public string? Reason { get; set; }
}

/// <summary>Body of <c>POST api/public/supplier-bookings/reschedule</c>: who the customer is, and the slot it wants.</summary>
public class SupplierBookingRescheduleRequest : SupplierBookingAccessRequest
{
    /// <summary>The start of the slot, exactly as <c>GET api/public/suppliers/{slug}/slots</c> gave it (<c>startUtc</c>).</summary>
    [Required(ErrorMessage = "SupplierBookingStartRequired")]
    public DateTime? StartUtc { get; set; }
}

/// <summary>
/// Answer of every call of the customer's own area (200): the booking as it is now and what the customer can do with it. Built from
/// <see cref="ShowcaseBookingView"/>; times are UTC instants with their Rome offset next to them (the two passes of the hour that
/// happens twice when the clocks go back are told apart by the offset).
/// </summary>
public class PublicSupplierBookingViewResponse
{
    /// <summary>The code of the booking as people read it (<c>XXXXX-XXXXX</c>).</summary>
    public string PublicCode { get; set; } = string.Empty;

    /// <summary>
    /// <c>Richiesto</c> (the supplier has not answered), <c>PresoInCarico</c> (accepted), <c>InCorso</c>, <c>Completato</c>,
    /// <c>Pagato</c>, <c>Rifiutato</c> or <c>Annullato</c>.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    public PublicSupplierBookingServiceDto Service { get; set; } = new();

    public PublicSupplierBookingSupplierDto Supplier { get; set; } = new();

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    /// <summary>Where the work is: the comune always, the exact address only once the supplier took the request.</summary>
    public PublicSupplierBookingPlaceDto Place { get; set; } = new();

    public PublicSupplierBookingPriceDto Price { get; set; } = new();

    /// <summary>When the supplier has to answer by, while the request is new and has no proposal; after it the request is cancelled.</summary>
    public DateTime? RespondBy { get; set; }

    /// <summary>The other time the supplier proposed and the customer has not answered; <c>null</c> when there is none.</summary>
    public PublicSupplierBookingProposalDto? Proposal { get; set; }

    /// <summary>Set when the booking is cancelled (<c>Annullato</c>).</summary>
    public PublicSupplierBookingCancellationDto? Cancellation { get; set; }

    /// <summary>Why the supplier refused the request (<c>Rifiutato</c>), when it said.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>Until when a cancellation is free (decision D6: a later one costs nothing either in v1).</summary>
    public PublicSupplierBookingCancellationTermsDto CancellationTerms { get; set; } = new();

    /// <summary>What the customer may do now: the page shows exactly these, and the endpoints enforce the same rules.</summary>
    public PublicSupplierBookingActionsDto Actions { get; set; } = new();
}

public class PublicSupplierBookingServiceDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The slug of the service while it is published (the page asks its free slots to move the booking); <c>null</c> after.</summary>
    public string? Slug { get; set; }
}

public class PublicSupplierBookingSupplierDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;
}

public class PublicSupplierBookingPlaceDto
{
    public string City { get; set; } = string.Empty;

    public string? PostalCode { get; set; }

    /// <summary>Street and number: only once the supplier took the request (and while the retention has not removed it).</summary>
    public string? Address { get; set; }

    public string? Floor { get; set; }

    public string? AccessNotes { get; set; }
}

public class PublicSupplierBookingPriceDto
{
    public string Currency { get; set; } = "EUR";

    public int? EstimatedAmountCents { get; set; }

    public int? QuotedAmountCents { get; set; }

    public int? FinalAmountCents { get; set; }

    /// <summary>The amount to show: the final total, else the supplier's quote, else the estimate; <c>null</c> is "to agree with the supplier".</summary>
    public int? AmountCents { get; set; }

    /// <summary><c>final</c>, <c>quote</c> or <c>estimate</c>: which of the three <see cref="AmountCents"/> is.</summary>
    public string? Basis { get; set; }

    public List<PublicSupplierBookingPriceLineDto> Lines { get; set; } = [];
}

public class PublicSupplierBookingPriceLineDto
{
    /// <summary><c>service</c>, <c>option</c> or <c>extra</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int UnitAmountCents { get; set; }

    public int AmountCents { get; set; }
}

public class PublicSupplierBookingProposalDto
{
    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    public DateTime ProposedAt { get; set; }

    /// <summary>The instant the customer has to answer by; after it the request is cancelled.</summary>
    public DateTime? AnswerBy { get; set; }

    /// <summary>What the supplier wrote with the proposal.</summary>
    public string? Message { get; set; }
}

public class PublicSupplierBookingCancellationDto
{
    public DateTime At { get; set; }

    /// <summary><c>customer</c>, <c>supplier</c> or <c>system</c> (nobody answered in time, or the customer let a proposed time lapse).</summary>
    public string By { get; set; } = string.Empty;

    /// <summary>The text a person wrote; <c>null</c> when the cancellation has none.</summary>
    public string? Reason { get; set; }
}

public class PublicSupplierBookingCancellationTermsDto
{
    public DateTime FreeUntilUtc { get; set; }

    public DateTimeOffset FreeUntilLocal { get; set; }

    /// <summary>True while a cancellation is still inside the free notice.</summary>
    public bool IsFree { get; set; }
}

public class PublicSupplierBookingActionsDto
{
    public bool CanCancel { get; set; }

    public bool CanReschedule { get; set; }

    public bool CanRespondToProposal { get; set; }
}

/// <summary>Maps the customer's view of a booking to the API DTO.</summary>
public static class PublicSupplierBookingViewMapper
{
    public static PublicSupplierBookingViewResponse ToDto(ShowcaseBookingView view) =>
        new()
        {
            PublicCode = BookingCodes.Format(view.PublicCode),
            Status = view.Status.ToString(),
            Service = new PublicSupplierBookingServiceDto { Name = view.Service.Name, Slug = view.Service.Slug },
            Supplier = new PublicSupplierBookingSupplierDto { Name = view.Supplier.Name, Slug = view.Supplier.Slug },
            StartUtc = AsUtc(view.StartUtc),
            EndUtc = AsUtc(view.EndUtc),
            StartLocal = RomeCalendar.ToRome(view.StartUtc),
            EndLocal = RomeCalendar.ToRome(view.EndUtc),
            Place = new PublicSupplierBookingPlaceDto
            {
                City = view.Place.City,
                PostalCode = view.Place.PostalCode,
                Address = view.Place.Address,
                Floor = view.Place.Floor,
                AccessNotes = view.Place.AccessNotes,
            },
            Price = new PublicSupplierBookingPriceDto
            {
                EstimatedAmountCents = view.Price.EstimatedAmountCents,
                QuotedAmountCents = view.Price.QuotedAmountCents,
                FinalAmountCents = view.Price.FinalAmountCents,
                AmountCents = view.Price.AmountCents,
                Basis = view.Price.Basis,
                Lines = view.Price.Lines
                    .Select(line => new PublicSupplierBookingPriceLineDto
                    {
                        Kind = line.Kind,
                        Label = line.Label,
                        Quantity = line.Quantity,
                        UnitAmountCents = line.UnitAmountCents,
                        AmountCents = line.AmountCents,
                    })
                    .ToList(),
            },
            RespondBy = view.RespondBy is { } respondBy ? AsUtc(respondBy) : null,
            Proposal = view.Proposal is { } proposal
                ? new PublicSupplierBookingProposalDto
                {
                    StartUtc = AsUtc(proposal.StartUtc),
                    EndUtc = AsUtc(proposal.EndUtc),
                    StartLocal = RomeCalendar.ToRome(proposal.StartUtc),
                    EndLocal = RomeCalendar.ToRome(proposal.EndUtc),
                    ProposedAt = AsUtc(proposal.ProposedAt),
                    AnswerBy = proposal.AnswerBy is { } answerBy ? AsUtc(answerBy) : null,
                    Message = proposal.Message,
                }
                : null,
            Cancellation = view.Cancellation is { } cancellation
                ? new PublicSupplierBookingCancellationDto
                {
                    At = AsUtc(cancellation.At),
                    By = PartyOf(cancellation.By),
                    Reason = cancellation.Reason,
                }
                : null,
            RejectionReason = view.RejectionReason,
            CancellationTerms = new PublicSupplierBookingCancellationTermsDto
            {
                FreeUntilUtc = AsUtc(view.CancellationTerms.FreeUntilUtc),
                FreeUntilLocal = RomeCalendar.ToRome(view.CancellationTerms.FreeUntilUtc),
                IsFree = view.CancellationTerms.IsFree,
            },
            Actions = new PublicSupplierBookingActionsDto
            {
                CanCancel = view.Actions.CanCancel,
                CanReschedule = view.Actions.CanReschedule,
                CanRespondToProposal = view.Actions.CanRespondToProposal,
            },
        };

    /// <summary>The party that cancelled, as the page names it. The host never cancels a showcase request.</summary>
    internal static string PartyOf(ServiceRequestActorParty party) => party switch
    {
        ServiceRequestActorParty.Customer => "customer",
        ServiceRequestActorParty.Supplier => "supplier",
        _ => "system",
    };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
