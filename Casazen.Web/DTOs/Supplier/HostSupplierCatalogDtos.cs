using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>A published service of a supplier as the host reads it to ask for an intervention (SR-08a): see <see cref="HostSupplierService"/>.</summary>
public class HostSupplierServiceDto
{
    /// <summary>The service: <c>serviceListingId</c> of the request.</summary>
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A code of <c>GET api/service-categories</c>.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Indicative duration in minutes; <c>null</c> when the supplier gave none (the host then gives the time and the length).</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>The "from" price in euro cents; <c>null</c> is "on quote".</summary>
    public int? PriceFromCents { get; set; }

    /// <summary><c>PerJob</c>, <c>PerHour</c>, <c>PerSet</c> or <c>PerSquareMeter</c>.</summary>
    public SupplierServicePriceUnit PriceUnit { get; set; }

    /// <summary>The host waits for the supplier's offer: the price is confirmed before the work.</summary>
    public bool RequiresQuote { get; set; }

    /// <summary>
    /// The price a request for this service starts with: the "from" price of a service priced per job; <c>null</c> for one on quote or
    /// priced per hour, set or square meter.
    /// </summary>
    public int? EstimatedAmountCents { get; set; }

    public static HostSupplierServiceDto From(HostSupplierService service) => new()
    {
        Id = service.Id,
        Name = service.Name,
        Category = service.Category,
        DurationMinutes = service.DurationMinutes,
        PriceFromCents = service.PriceFromCents,
        PriceUnit = service.PriceUnit,
        RequiresQuote = service.RequiresQuote,
        EstimatedAmountCents = service.EstimatedAmountCents,
    };
}

/// <summary>The published services of a supplier for a property of the host (SR-08a).</summary>
public class HostSupplierServicesResponse
{
    public IEnumerable<HostSupplierServiceDto> Items { get; set; } = [];
}

/// <summary>One free slot of a service: the work can start at <see cref="StartUtc"/> and ends at <see cref="EndUtc"/> (UTC).</summary>
public class HostSupplierSlotDto
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
}

/// <summary>
/// The free slots of a service of a supplier (SR-08a), in start order, for the Europe/Rome days <see cref="From"/> to <see cref="To"/>
/// (both included; both <c>null</c> when the first day asked for is past the supplier's horizon, or the service has no duration).
/// </summary>
public class HostSupplierSlotsResponse
{
    public Guid ServiceId { get; set; }

    /// <summary>How long the work lasts, in minutes; <c>null</c> when the service has no duration (then there is no slot).</summary>
    public int? DurationMinutes { get; set; }

    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    public IEnumerable<HostSupplierSlotDto> Slots { get; set; } = [];

    public static HostSupplierSlotsResponse Of(HostSupplierSlots slots) => new()
    {
        ServiceId = slots.ServiceId,
        DurationMinutes = slots.DurationMinutes,
        From = slots.From,
        To = slots.To,
        Slots = slots.Slots.Select(s => new HostSupplierSlotDto { StartUtc = s.StartUtc, EndUtc = s.EndUtc }).ToList(),
    };
}
