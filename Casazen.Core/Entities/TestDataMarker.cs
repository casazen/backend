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

    /// <summary>
    /// A CRM profile (<see cref="GuestCrmProfile"/>, E07/F01, NF-88) that the CRM sync wrote from the showcase bookings. The reset deletes it, and its
    /// contacts and proposed merges with it (they follow their profile); a profile the sync made for a real guest has no marker and is never touched.
    /// </summary>
    public const string CrmProfile = "CrmProfile";

    /// <summary>A contact (<see cref="GuestContact"/>, NF-89) of a showcase profile: written by the CRM sync, deleted with its profile at the reset.</summary>
    public const string CrmContact = "CrmContact";

    /// <summary>A merge proposed for a showcase profile (<see cref="GuestMergeCandidate"/>, NF-88): written by the CRM sync, deleted with its profile at the reset.</summary>
    public const string CrmMergeCandidate = "CrmMergeCandidate";

    /// <summary>
    /// A row of the privacy history of a showcase guest (<see cref="GuestConsentRecord"/>: the soft opt-in notice a direct customer was shown, the
    /// marketing consent of the online check-in). It goes with its guest at the reset.
    /// </summary>
    public const string GuestConsent = "GuestConsentRecord";

    /// <summary>The per-channel marketing consent of the online check-in of a showcase guest (<see cref="MarketingConsentRecord"/>); it goes with its booking at the reset.</summary>
    public const string MarketingConsent = "MarketingConsentRecord";

    /// <summary>
    /// Who an invoice of a showcase stay is made out to (<see cref="BookingInvoiceData"/>, E06/F02/US01, NF-81): written with the booking at the
    /// reset, invented and marked TEST. It goes with its booking (the table cascades), so the reset deletes it with the stay; a real row has no
    /// marker and is never touched.
    /// </summary>
    public const string BookingInvoiceData = "BookingInvoiceData";

    /// <summary>
    /// The profile of the issuer of the documents of the demo org (<see cref="OrgFiscalIssuerProfile"/>, E06/F02/US02, NF-82): written at the
    /// reset only when the org has no profile of its own, and deleted by the next reset with its marker. A real profile has no marker and is
    /// never touched.
    /// </summary>
    public const string FiscalIssuerProfile = "OrgFiscalIssuerProfile";
}
