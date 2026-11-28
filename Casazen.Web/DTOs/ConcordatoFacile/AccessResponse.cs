namespace Casazen.Web.Dtos.ConcordatoFacile;

public class AccessResponse
{
    public bool HasAccess { get; set; }

    public string? Source { get; set; }

    public bool GiftAvailable { get; set; }

    public bool GiftConsumed { get; set; }

    public DateTime? PurchasedAt { get; set; }

    public DateTime? GiftAssignedAt { get; set; }

    public bool PrivacyAccepted { get; set; }

    public DateTime? PrivacyAcceptedAt { get; set; }

    public DateTime? LinkOpenedAt { get; set; }
}
