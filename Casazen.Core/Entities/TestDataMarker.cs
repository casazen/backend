using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Points at one row written by the demo seed. A reset deletes only rows that have a marker of origin
/// <see cref="TestDataOrigins.Showcase"/>. Compliance fixtures on a host's own property are marked too, and are left
/// in place: the CIN and the files are invented, the property is not.
/// </summary>
[Table("TestDataMarkers")]
public class TestDataMarker : ITenantOwned
{
    public const int EntityTypeMaxLength = 64;
    public const int OriginMaxLength = 32;
    public const int TemplateKeyMaxLength = 80;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(EntityTypeMaxLength)]
    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    /// <summary><see cref="TestDataOrigins.Compliance"/> or <see cref="TestDataOrigins.Showcase"/>.</summary>
    [Required, MaxLength(OriginMaxLength)]
    public string Origin { get; set; } = string.Empty;

    /// <summary>Stable key inside <c>DemoShowcaseCatalog</c> when <see cref="Origin"/> is the showcase.</summary>
    [MaxLength(TemplateKeyMaxLength)]
    public string? TemplateKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Why a row was marked as test data.</summary>
public static class TestDataOrigins
{
    /// <summary>Invented CIN, document or checklist written onto a property that already existed.</summary>
    public const string Compliance = "compliance";

    /// <summary>Row created by the showcase template. The reset deletes these and recreates them.</summary>
    public const string Showcase = "showcase";
}

/// <summary>Values of <see cref="TestDataMarker.EntityType"/>. Append a new one when the seed writes a new table.</summary>
public static class TestDataEntities
{
    public const string Property = "Property";
    public const string PropertyDocument = "PropertyDocument";
    public const string SafetyChecklist = "PropertySafetyChecklist";
    public const string Guest = "Guest";
    public const string Booking = "Booking";
    public const string HostInvite = "HostInviteRecord";
    public const string PropertyOwner = "PropertyOwner";

    /// <summary>The state of a property on one channel of the channel manager (NF-27, NF-38); goes with its property.</summary>
    public const string ListingChannelState = "ListingChannelState";

    /// <summary>The sending of one photo of a property to the channel manager (NF-32); goes with its property.</summary>
    public const string PhotoUploadTask = "PhotoUploadTask";

    /// <summary>A room of a property with its beds (NF-34); goes with its property.</summary>
    public const string PropertyRoom = "PropertyRoom";

    /// <summary>A length-of-stay discount tier of a property (NF-35); goes with its property.</summary>
    public const string PropertyStayDiscount = "PropertyStayDiscount";

    /// <summary>
    /// A popup alert of the demo host (NF-08, ADR-025). It points at a demo stay or house by id and has no foreign key to it, so the reset
    /// deletes it by its marker before it deletes the stays and the houses.
    /// </summary>
    public const string PopupAlert = "PopupAlert";

    /// <summary>A saved draft of a wizard (<see cref="WizardDraft"/>): the demo host's, written by the showcase and deleted by its reset.</summary>
    public const string WizardDraft = "WizardDraft";

    /// <summary>
    /// A payment of the tourist tax to a comune, with its receipt (<see cref="TouristTaxRemittance"/>, NF-100): the demo host's, written by
    /// the showcase and deleted by its reset together with its invented receipt. A real remittance never carries a marker, so the reset
    /// never touches it.
    /// </summary>
    public const string TouristTaxRemittance = "TouristTaxRemittance";

    /// <summary>
    /// A partner of the marketplace catalog (<see cref="MarketplacePartner"/>, E09/F01): platform data, shared by every org of the installation, so
    /// the showcase creates it only when the catalog has no partner with its code, and the reset of the org that created it deletes it (its texts go
    /// with it). It is named TEST and points at an example address, so it is obvious in the catalog of a test environment; the showcase exists
    /// only outside Production.
    /// </summary>
    public const string MarketplacePartner = "MarketplacePartner";

    /// <summary>The texts of a showcase partner (<see cref="MarketplacePartnerText"/>); they go with the partner.</summary>
    public const string MarketplacePartnerText = "MarketplacePartnerText";

    /// <summary>
    /// An activation of the marketplace of the demo org (<see cref="MarketplaceActivation"/>, E09/F01): the partner it declared activated, deleted by
    /// the reset with the other showcase rows. A real activation has no marker and is never touched.
    /// </summary>
    public const string MarketplaceActivation = "MarketplaceActivation";

    /// <summary>A registered guest of a demo stay (<see cref="Entities.StayGuest"/>, NF-40); goes with its booking.</summary>
    public const string StayGuest = "StayGuest";

    /// <summary>
    /// The rate the demo host declared for a comune with no official one (<see cref="Entities.OrgTouristTaxRate"/>, NF-46): tenant data of the
    /// demo org, deleted by the reset. A real declared rate has no marker and is never touched.
    /// </summary>
    public const string OrgTouristTaxRate = "OrgTouristTaxRate";

    /// <summary>
    /// A channel rule of the tourist tax (<see cref="Entities.TouristTaxChannelRule"/>, ADR-037-R3): platform data, written by the showcase only for
    /// the invented comune of the demo (ISTAT 999990) and only when no rule exists for it, deleted by the reset of the org that created it.
    /// </summary>
    public const string TouristTaxChannelRule = "TouristTaxChannelRule";
}
