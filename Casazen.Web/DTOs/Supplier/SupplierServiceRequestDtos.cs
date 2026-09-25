using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>
/// A service request in the supplier console (SU-08, A4-14): <c>GET /api/supplier/inbox</c> items. What the supplier sees
/// before and after the take is in <see cref="SupplierJobDisclosure"/>; the guest of the stay is never included.
/// </summary>
public class SupplierServiceRequestDto
{
    public Guid Id { get; set; }

    /// <summary><c>ShortRent</c> (for a stay) or <c>LongRent</c> (for the property), decision D2.</summary>
    public string RentalContext { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Urgency { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? TakenAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? PaidAt { get; set; }

    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;

    /// <summary>Comune of the property: always shown.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Postal code (zone within the comune): always shown when the host entered it.</summary>
    public string? PostalCode { get; set; }

    /// <summary>Street address: only once the supplier took the request (<see cref="ContactDisclosed"/>).</summary>
    public string? Address { get; set; }

    /// <summary>
    /// Europe/Rome calendar date of the job (<c>YYYY-MM-DD</c>): the check-out day of the stay for a short-rent request;
    /// null when there is no stay (long-rent, or an older request not traced to a stay): to agree with the host.
    /// </summary>
    public DateOnly? ScheduledFor { get; set; }

    /// <summary>The stay (short-rent): booking id and dates only, never the guest.</summary>
    public SupplierStayDto? Stay { get; set; }

    /// <summary>True once the supplier took the request: address and host contact are shown.</summary>
    public bool ContactDisclosed { get; set; }

    /// <summary>The host contact: only when <see cref="ContactDisclosed"/>.</summary>
    public SupplierHostContactDto? HostContact { get; set; }
}

/// <summary><c>GET /api/supplier/inbox/{id}</c>: the request and its history (SU-08).</summary>
public class SupplierServiceRequestDetailDto : SupplierServiceRequestDto
{
    /// <summary>The transitions, oldest first, with date and actor.</summary>
    public IEnumerable<ServiceRequestHistoryEntryDto> History { get; set; } = [];
}

/// <summary>The stay of a short-rent request as the supplier sees it: no guest data.</summary>
public class SupplierStayDto
{
    public Guid BookingId { get; set; }

    /// <summary>Check-in day (<c>YYYY-MM-DD</c>, Europe/Rome).</summary>
    public DateOnly CheckIn { get; set; }

    /// <summary>Check-out day (<c>YYYY-MM-DD</c>, Europe/Rome).</summary>
    public DateOnly CheckOut { get; set; }
}

/// <summary>Host contact given to the supplier after the take.</summary>
public class SupplierHostContactDto
{
    /// <summary>Display name of the host org.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Contact email of the host org.</summary>
    public string? Email { get; set; }

    /// <summary>Phone of the property owner's profile, when filled in.</summary>
    public string? Phone { get; set; }
}

/// <summary>One step of the history of a service request.</summary>
public class ServiceRequestHistoryEntryDto
{
    /// <summary>The status reached (<c>Richiesto</c>, <c>PresoInCarico</c>, <c>Completato</c>, <c>Pagato</c>, <c>Rifiutato</c>).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>UTC instant of the transition.</summary>
    public DateTime At { get; set; }

    /// <summary><c>Host</c> or <c>Supplier</c>.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>The supplier member who took the request, when known; host members are never named.</summary>
    public string? ActorName { get; set; }

    /// <summary>The rejection reason, on the <c>Rifiutato</c> step.</summary>
    public string? Reason { get; set; }
}

/// <summary>Maps the supplier view of a request (<see cref="SupplierServiceRequestView"/>) to the API DTOs.</summary>
public static class SupplierServiceRequestMapper
{
    public static SupplierServiceRequestDto ToDto(SupplierServiceRequestView view) =>
        Fill(new SupplierServiceRequestDto(), view);

    public static SupplierServiceRequestDetailDto ToDetailDto(SupplierServiceRequestView view)
    {
        var dto = Fill(new SupplierServiceRequestDetailDto(), view);
        dto.History = (view.History ?? []).Select(h => new ServiceRequestHistoryEntryDto
        {
            Status = h.Status.ToString(),
            At = h.At,
            Actor = h.Actor.ToString(),
            ActorName = h.ActorName,
            Reason = h.Reason,
        }).ToList();
        return dto;
    }

    private static T Fill<T>(T dto, SupplierServiceRequestView view)
        where T : SupplierServiceRequestDto
    {
        dto.Id = view.Id;
        dto.RentalContext = view.RentalContext.ToString();
        dto.Status = view.Status.ToString();
        dto.Category = view.Category;
        dto.Urgency = view.Urgency.ToString();
        dto.Notes = view.Notes;
        dto.RejectionReason = view.RejectionReason;
        dto.CreatedAt = view.CreatedAt;
        dto.UpdatedAt = view.UpdatedAt;
        dto.TakenAt = view.TakenAt;
        dto.CompletedAt = view.CompletedAt;
        dto.PaidAt = view.PaidAt;
        dto.PropertyId = view.Location.PropertyId;
        dto.PropertyName = view.Location.PropertyName;
        dto.City = view.Location.City;
        dto.PostalCode = view.Location.PostalCode;
        dto.Address = view.Location.Address;
        dto.ScheduledFor = view.ScheduledFor;
        dto.Stay = view.Stay is null
            ? null
            : new SupplierStayDto { BookingId = view.Stay.BookingId, CheckIn = view.Stay.CheckIn, CheckOut = view.Stay.CheckOut };
        dto.ContactDisclosed = view.ContactDisclosed;
        dto.HostContact = view.HostContact is null
            ? null
            : new SupplierHostContactDto
            {
                Name = view.HostContact.Name,
                Email = view.HostContact.Email,
                Phone = view.HostContact.Phone,
            };
        return dto;
    }
}
