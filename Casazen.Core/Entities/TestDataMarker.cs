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

    [Required, MaxLength(OriginMaxLength)]
    public string Origin { get; set; } = string.Empty;

    [MaxLength(TemplateKeyMaxLength)]
    public string? TemplateKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class TestDataOrigins
{
    public const string Compliance = "compliance";

    public const string Showcase = "showcase";
}

public static class TestDataEntities
{
    public const string Property = "Property";
    public const string PropertyDocument = "PropertyDocument";
    public const string SafetyChecklist = "PropertySafetyChecklist";
    public const string Guest = "Guest";
    public const string Booking = "Booking";
    public const string HostInvite = "HostInviteRecord";
    public const string PropertyOwner = "PropertyOwner";

    public const string ListingChannelState = "ListingChannelState";

    public const string PhotoUploadTask = "PhotoUploadTask";

    public const string PropertyRoom = "PropertyRoom";

    public const string PropertyStayDiscount = "PropertyStayDiscount";

    public const string PopupAlert = "PopupAlert";

    public const string WizardDraft = "WizardDraft";

    /// <summary>
    /// A payment of the tourist tax to a comune, with its receipt (<see cref="TouristTaxRemittance"/>, NF-100): the demo host's, written by
    /// the showcase and deleted by its reset together with its invented receipt. A real remittance never carries a marker, so the reset
    /// never touches it.
    /// </summary>
    public const string TouristTaxRemittance = "TouristTaxRemittance";

    public const string MarketplacePartner = "MarketplacePartner";

    public const string MarketplacePartnerText = "MarketplacePartnerText";

    public const string MarketplaceActivation = "MarketplaceActivation";

    public const string StayGuest = "StayGuest";

    public const string OrgTouristTaxRate = "OrgTouristTaxRate";

    /// <summary>
    /// A channel rule of the tourist tax (<see cref="Entities.TouristTaxChannelRule"/>, ADR-037-R3): platform data, written by the showcase only for
    /// the invented comune of the demo (ISTAT 999990) and only when no rule exists for it, deleted by the reset of the org that created it.
    /// </summary>
    public const string TouristTaxChannelRule = "TouristTaxChannelRule";

    public const string CrmProfile = "CrmProfile";

    public const string CrmContact = "CrmContact";

    public const string CrmMergeCandidate = "CrmMergeCandidate";

    public const string GuestConsent = "GuestConsentRecord";

    public const string MarketingConsent = "MarketingConsentRecord";

    /// <summary>
    /// Who an invoice of a showcase stay is made out to (<see cref="BookingInvoiceData"/>, E06/F02/US01, NF-81): written with the booking at the
    /// reset, invented and marked TEST. It goes with its booking (the table cascades), so the reset deletes it with the stay; a real row has no
    /// marker and is never touched.
    /// </summary>
    public const string BookingInvoiceData = "BookingInvoiceData";

    public const string FiscalIssuerProfile = "OrgFiscalIssuerProfile";

    public const string WelcomeLink = "PropertyWelcomeLink";

    /// <summary>
    /// A conversation of a channel (<see cref="OtaMessageThread"/>, E05/F02/US01/T01) that the showcase wrote for the hub of messages: invented guests
    /// and words that say TEST, no Octorate behind them. It points at the demo house and stay by id with a foreign key that lets go (SET NULL), so the
    /// reset deletes it by its marker before it deletes the houses, or it would stay behind as an unassigned conversation; its messages go with it.
    /// </summary>
    public const string OtaMessageThread = "OtaMessageThread";

    public const string GuestThread = "GuestThread";

    public const string MessageTemplate = "MessageTemplate";

    public const string ScheduledMessage = "ScheduledMessage";

    public const string OctorateBookingSync = "OctorateBookingSync";

    /// <summary>
    /// The price and the rules of one night of a showcase house (<see cref="Entities.PropertyDayRule"/>, E08/F05/US01/T01, NF-36): the nights of the demo house
    /// that have their own price, a minimum stay or a closed arrival. The rows go with their house (cascade) and the reset deletes their markers
    /// with the other showcase markers; a rule the host wrote has no marker and is never touched.
    /// </summary>
    public const string PropertyDayRule = "PropertyDayRule";

    /// <summary>
    /// Who sends the police and ISTAT communications of a showcase house (<see cref="Entities.PropertyComplianceSender"/>, E03/F02/US01/T03, NF-52):
    /// the choice the demo host stored, so the traffic light reads the stay of that house as entrusted to the channel manager. It follows its house
    /// (cascade) and the reset deletes its marker with the other showcase markers; a real choice has no marker and is never touched.
    /// </summary>
    public const string PropertyComplianceSender = "PropertyComplianceSender";

    /// <summary>
    /// A guest of a showcase stay as sent to Octorate (<see cref="Entities.StayGuestOctorateLink"/>, E03/F02/US01/T03, NF-52): the row that says the
    /// channel manager holds the guest for good, invented like the rest and never sent anywhere. It goes with its booking (cascade).
    /// </summary>
    public const string StayGuestOctorateLink = "StayGuestOctorateLink";

    /// <summary>
    /// The receipt of the Alloggiati communication that the demo host uploaded (<see cref="Entities.ManualComplianceProof"/>, E03/F02/US01/T03,
    /// NF-52), with its invented one-page PDF in the private bucket. The row follows its house (cascade) and the reset removes the file; a real proof
    /// has no marker and is never touched.
    /// </summary>
    public const string ManualComplianceProof = "ManualComplianceProof";

    /// <summary>
    /// The flat the receipt of a showcase stay covers (<see cref="Entities.ManualComplianceProofProperty"/>, E20/F02/US01/T01, decision O3): the line every proof has for
    /// the flat it was filed under. It goes with its proof and its house (cascade) and the reset deletes its marker with the other showcase markers; a real line has no
    /// marker and is never touched.
    /// </summary>
    public const string ManualComplianceProofProperty = "ManualComplianceProofProperty";

    /// <summary>
    /// A line of the log of what was sent to the authorities for a showcase stay (<see cref="Entities.ComplianceSendLogEntry"/>, E03/F02/US03/T01, NF-47):
    /// the one the channel manager's send of the guests, or the host's declaration, would have written. Invented like the rest and sent nowhere. It
    /// follows its house (cascade); a real line has no marker and is never touched.
    /// </summary>
    public const string ComplianceSendLogEntry = "ComplianceSendLogEntry";

    // E10/F01/US01/T04: the tables of the activation of CasaZen with Octorate. The reset deletes only the rows of these tables that carry a showcase
    // marker of the org (before its houses), so a real row has no marker and is never touched.

    public const string OnboardingPartState = "OnboardingPartState";

    public const string PropertyFlowActivation = "PropertyFlowActivation";

    public const string OnboardingPanelStep = "OnboardingPanelStep";

    public const string OnboardingAssistRequest = "OnboardingAssistRequest";

    public const string OnboardingPilotMember = "OnboardingPilotMember";

    public const string PropertyOnboardingData = "PropertyOnboardingData";

    /// <summary>
    /// The test connection of the demo org to Octorate (<see cref="Entities.OctorateConnection"/>, no token): written only when the org has no
    /// connection of its own in the environment, deleted by the next reset. A real connection has no marker and is never touched.
    /// </summary>
    public const string OctorateConnection = "OctorateConnection";

    public const string OctorateListingMapping = "OctorateListingMapping";

    public const string Todo = "InternalActivity";

    public const string FiscalDocument = "FiscalDocument";

    /// <summary>
    /// A payment of a showcase stay (<see cref="Entities.Payment"/>, E07/F03/US02, NF-97): cash, a bank transfer or a card payment with the figures the provider
    /// would have reported, invented and saying TEST, so the reports of the money have something to show. It goes with its booking (cascade), and the reset deletes
    /// its marker with the other showcase markers; a real payment has no marker and is never touched.
    /// </summary>
    public const string Payment = "Payment";

    public const string Routine = "Routine";

    public const string Review = "Review";

    /// <summary>
    /// The review of a showcase Airbnb stay (<see cref="Entities.OtaReview"/>, E05/F06/US01/T03, NF-69): the row the sync of the reviews would write, invented and saying TEST, with no channel
    /// manager behind it. Deleted by the reset by its marker, with the draft of its reply; a real row has no marker and is never touched.
    /// </summary>
    public const string OtaReview = "OtaReview";

    public const string ApprovalQueueItem = "ApprovalQueueItem";
}
