using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Supplier;

public class HostSupplierServiceDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public int? DurationMinutes { get; set; }

    public int? PriceFromCents { get; set; }

    public SupplierServicePriceUnit PriceUnit { get; set; }

    public bool RequiresQuote { get; set; }

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

public class HostSupplierServicesResponse
{
    public IEnumerable<HostSupplierServiceDto> Items { get; set; } = [];
}

public class HostSupplierSlotDto
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
}

public class HostSupplierSlotsResponse
{
    public Guid ServiceId { get; set; }

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
