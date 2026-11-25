using System.ComponentModel.DataAnnotations;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.GuestPortal;

public enum GuestHostExtraPricing { Fixed, PerNight, PerGuest, PerUnit }
public enum GuestHostExtraCalendar { None, EarlyCheckIn, LateCheckOut }
public enum GuestHostExtraStatus { Requested, ReadyToPay, Paid, Declined, Fulfilled, Cancelled }

public sealed class GuestHostExtra : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    // A single organization catalog, with publication selected independently on each property.
    public Guid[] PropertyIds { get; set; } = [];
    [MaxLength(120)] public string Name { get; set; } = "";
    [MaxLength(2000)] public string Description { get; set; } = "";
    [Precision(12, 2)] public decimal Price { get; set; }
    public GuestHostExtraPricing Pricing { get; set; }
    public int? Inventory { get; set; }
    public int CutoffHours { get; set; }
    public bool DuringStay { get; set; }
    public bool RequiresApproval { get; set; }
    public GuestHostExtraCalendar Calendar { get; set; }
}

public sealed class GuestHostExtraOrder : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrgId { get; set; }
    public Guid BookingId { get; set; }
    public Guid ExtraId { get; set; }
    [MaxLength(120)] public string Name { get; set; } = "";
    public int Quantity { get; set; }
    [Precision(12, 2)] public decimal Amount { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "";
    public GuestHostExtraStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    [MaxLength(120)] public string? StripeIntentId { get; set; }
    [MaxLength(120)] public string? StripeAccountId { get; set; }
}
