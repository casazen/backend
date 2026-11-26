using System.ComponentModel.DataAnnotations;

namespace Casazen.Core.Entities;

/// <summary>A property's offer and the supplier's consent to its current terms.</summary>
public sealed class GuestSupplierOffer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostOrgId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid ServiceId { get; set; }
    public Guid SupplierOrgId { get; set; }
    public bool HostOrigin { get; set; }
    public int CommissionBasisPoints { get; set; }
    public bool Accepted { get; set; }
    public bool Enabled { get; set; }
    [Timestamp] public uint Version { get; set; }
}

/// <summary>An order with a fixed price and commission snapshot for the stay.</summary>
public sealed class GuestSupplierOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HostOrgId { get; set; }
    public Guid BookingId { get; set; }
    public Guid PropertyId { get; set; }
    public Guid OfferId { get; set; }
    public Guid SupplierOrgId { get; set; }
    public Guid RequestId { get; set; }
    public bool HostOrigin { get; set; }
    [MaxLength(250)] public required string ServiceName { get; set; }
    [MaxLength(250)] public required string SupplierName { get; set; }
    public int CommissionBasisPoints { get; set; }
    public int SupplierPriceCents { get; set; }
    public int CommissionCents { get; set; }
    public int RefundCents { get; set; }
    public DateTime? StripeCheckoutStartedAt { get; set; }
    public bool StripeSandbox { get; set; }
    [MaxLength(255)] public string? StripePaymentIntentId { get; set; }
    public GuestSupplierOrderState State { get; set; }
    public DateTime RequestedFor { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    [MaxLength(2000)] public string? RequestNote { get; set; }
    [MaxLength(2000)] public string? Complaint { get; set; }
    [MaxLength(2000)] public string? SupplierReply { get; set; }
    [MaxLength(2000)] public string? Decision { get; set; }
    [Timestamp] public uint Version { get; set; }
}

public enum GuestSupplierOrderState
{
    PendingQuote,
    Quoted,
    Authorized,
    CardSaved,
    Accepted,
    Declined,
    Completed,
    Disputed,
    Resolved,
    Released
}
