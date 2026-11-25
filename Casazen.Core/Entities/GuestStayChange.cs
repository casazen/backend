using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

public sealed class GuestStayChange : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    public Guid BookingId { get; set; }
    public DateTime OriginalArrival { get; set; }
    public DateTime OriginalDeparture { get; set; }
    public int OriginalGuests { get; set; }
    public int OriginalChildren { get; set; }
    public DateTime Arrival { get; set; }
    public DateTime Departure { get; set; }
    public int Adults { get; set; }
    public int[] ChildrenAges { get; set; } = [];
    public decimal OriginalTotal { get; set; }
    public decimal ProposedTotal { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
}
