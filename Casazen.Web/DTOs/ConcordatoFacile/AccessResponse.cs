namespace Casazen.Web.Dtos.ConcordatoFacile;

/// <summary>
/// Response from checking ConcordatoFacile access for a lease (ADR-005-R8).
/// Indicates whether the user has access and through what source (payment, gift, or admin).
/// </summary>
public class AccessResponse
{
    /// <summary>Whether the user has access to ConcordatoFacile for this lease</summary>
    public bool HasAccess { get; set; }

    /// <summary>Source of access: "payment", "gift", "admin", or null if no access</summary>
    public string? Source { get; set; }

    /// <summary>Whether the user's org has an available (unconsumed) gift</summary>
    public bool GiftAvailable { get; set; }

    /// <summary>Whether a gift was consumed for this specific lease</summary>
    public bool GiftConsumed { get; set; }

    /// <summary>When the payment was completed (ISO8601), if access is via payment</summary>
    public DateTime? PurchasedAt { get; set; }

    /// <summary>When the gift was granted (ISO8601), if access is via gift</summary>
    public DateTime? GiftAssignedAt { get; set; }
}
