using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Demo;

/// <summary>
/// The showcase a client walks through. Every feature that adds a screen, an entity or a flow a host or a guest can
/// see adds a scenario here, in the same change, and extends <see cref="Scenarios"/>. The rule is in
/// <c>.claude/rules/demo-seed.md</c>.
/// </summary>
/// <remarks>
/// Places use the ISTAT codes the rest of the product already treats as examples (Roma <c>058091</c>) or the official
/// codes of Como and Milano. At reset the comuni table wins when the city is in it, so the region and the required
/// documents stay the ones of that comune.
/// </remarks>
public static class DemoShowcaseCatalog
{
    public const string Version = "2026-10-29.1";

    /// <summary>The body of a reset must carry this word. It is not a password: it only stops an accidental click.</summary>
    public const string ResetConfirmation = "RESET";

    public const string DraftKey = "monolocale-bozza";

    public static class Scenario
    {
        public const string PublishedLombardia = "short-stay-published-lombardia";
        public const string PublishedLazio = "short-stay-published-lazio";
        public const string ActivationPending = "short-stay-activation-pending";
        public const string LongStayCadastral = "long-stay-cadastral";
        public const string BookingUpcoming = "booking-upcoming";
        public const string BookingPast = "booking-past";
        public const string BookingManualLight = "booking-manual-light";
        public const string HostInvitePending = "host-invite-pending";
        public const string HostInviteUsed = "host-invite-used";
        public const string HostInviteExpired = "host-invite-expired";
        public const string PropertyOwnerAgency = "property-owner-agency";
        public const string AgencyInvoiceIssued = "agency-invoice-issued";
        public const string CampaignDraft = "campaign-draft";
        public const string CampaignApproved = "campaign-approved";
        public const string CampaignSent = "campaign-sent";
        public const string AgencyPropertiesCrossRegion = "agency-properties-cross-region";
        public const string OwnerShares = "owner-shares";
        public const string OwnerMandate = "owner-mandate";
        public const string StaffCollaborator = "staff-collaborator";
        public const string TrustedSupplier = "trusted-supplier";
        public const string BookingOtaIcal = "booking-ota-ical";
        public const string CheckInPending = "checkin-pending";
        public const string CheckInBiometricPassed = "checkin-biometric-passed";
        public const string CheckInOperatorVerified = "checkin-operator-verified";
        public const string CheckInRejected = "checkin-rejected";
        public const string AlloggiatiSentReceiptPending = "alloggiati-sent-receipt-pending";
        public const string AlloggiatiRegistered = "alloggiati-registered";
        public const string StatementPublished = "statement-published";
        public const string WithholdingSummary = "withholding-summary";
        public const string F24Record = "f24-record";
        public const string WireBatchGenerated = "wire-batch-generated";
        public const string RoutineActive = "routine-active";
        public const string RoutineTestMode = "routine-test-mode";
    }

    /// <summary>Scenarios the template must cover. A new feature appends its id and a property or booking that carries it.</summary>
    public static IReadOnlyList<string> Scenarios { get; } =
    [
        Scenario.PublishedLombardia,
        Scenario.PublishedLazio,
        Scenario.ActivationPending,
        Scenario.LongStayCadastral,
        Scenario.BookingUpcoming,
        Scenario.BookingPast,
        Scenario.BookingManualLight,
        Scenario.HostInvitePending,
        Scenario.HostInviteUsed,
        Scenario.HostInviteExpired,
        Scenario.PropertyOwnerAgency,
        Scenario.AgencyInvoiceIssued,
        Scenario.CampaignDraft,
        Scenario.CampaignApproved,
        Scenario.CampaignSent,
        Scenario.AgencyPropertiesCrossRegion,
        Scenario.OwnerShares,
        Scenario.OwnerMandate,
        Scenario.StaffCollaborator,
        Scenario.TrustedSupplier,
        Scenario.BookingOtaIcal,
        Scenario.CheckInPending,
        Scenario.CheckInBiometricPassed,
        Scenario.CheckInOperatorVerified,
        Scenario.CheckInRejected,
        Scenario.AlloggiatiSentReceiptPending,
        Scenario.AlloggiatiRegistered,
        Scenario.StatementPublished,
        Scenario.WithholdingSummary,
        Scenario.F24Record,
        Scenario.WireBatchGenerated,
        Scenario.RoutineActive,
        Scenario.RoutineTestMode,
    ];

    /// <summary>
    /// Invites of new independent hosts the demo host sent: one waiting, one accepted, one expired, so the list of the host shows every
    /// state. Addresses are <c>@example.com</c>; the link token is random and never shown, so none of them can be accepted.
    /// </summary>
    public static IReadOnlyList<DemoHostInviteTemplate> HostInvites { get; } =
    [
        new("in-attesa", "host.demo.in-attesa@example.com", "Ciao, provaci anche tu (invito di dimostrazione).", -1, DemoHostInviteState.Pending, Scenario.HostInvitePending),
        new("accettato", "host.demo.accettato@example.com", null, -10, DemoHostInviteState.Used, Scenario.HostInviteUsed),
        new("scaduto", "host.demo.scaduto@example.com", null, -20, DemoHostInviteState.Expired, Scenario.HostInviteExpired),
    ];

    /// <summary>
    /// Property owners of the agency demo (ADR-012): multiple owners with fiscal code and IBAN to exercise the
    /// encryption, ownership shares, mandates with commission rates, the list endpoint with masked IBAN, and the financial read endpoint.
    /// </summary>
    public static IReadOnlyList<DemoPropertyOwnerTemplate> PropertyOwners { get; } =
    [
        new("mario-rossi", "Mario", "Rossi", "proprietario.demo@example.com", "+39 333 1234567",
            "RSSMRA70A01H501X", "IT60X0542811101000000123456", OwnerTaxRegime.Private, 100m, 15m, Scenario.PropertyOwnerAgency),
        new("giulia-bianchi", "Giulia", "Bianchi", "giulia.proprietario@example.com", "+39 334 9876543",
            "BNCGLI75C42F205Z", "IT28W8000000292100645211151", OwnerTaxRegime.Private, 50m, 12m, Scenario.OwnerShares),
        new("luca-verdi", "Luca", "Verdi", "luca.proprietario@example.com", "+39 335 1122334",
            "VRDLCU80D15F205A", "IT10J0200805017000105432100", OwnerTaxRegime.Private, 50m, 12m, Scenario.OwnerShares),
        new("anna-ferrari", "Anna", "Ferrari", "anna.proprietario@example.com", "+39 336 5544332",
            "FRRNNA85E50H501B", "IT07X0300203280284975071923", OwnerTaxRegime.VatHolder, 100m, 18m, Scenario.OwnerMandate),
    ];

    /// <summary>
    /// Agency invoices demo (ADR-019): one issued invoice to demonstrate the invoice generation flow.
    /// Series is the year, invoice number is sequential, amounts are test data.
    /// </summary>
    public static IReadOnlyList<DemoAgencyInvoiceTemplate> AgencyInvoices { get; } =
    [
        new("fattura-001", "mario-rossi", "2026", "1", 10, 2026, 100.00m, 22.00m, Scenario.AgencyInvoiceIssued),
    ];

    /// <summary>
    /// Guest segments for marketing campaigns (NF-24): simple condition to demo segment UI and recipient filtering.
    /// Empty condition JSON matches all guests (with consent filtering applied at send time).
    /// </summary>
    public static IReadOnlyList<DemoGuestSegmentTemplate> GuestSegments { get; } =
    [
        new("ospiti-fedeli", "Ospiti fedeli (DEMO)", "{\"version\":\"1.0\",\"groups\":[]}", Scenario.CampaignDraft),
    ];

    /// <summary>
    /// Marketing campaigns (NF-24): one in each status to demonstrate the campaign lifecycle.
    /// Email addresses are @example.com, data clearly marked as TEST.
    /// </summary>
    public static IReadOnlyList<DemoCampaignTemplate> Campaigns { get; } =
    [
        new("bozza", "ospiti-fedeli", "Offerta TEST - Sconto primavera (BOZZA)",
            "<p>Caro ospite,</p><p>Ti offriamo uno sconto del 15% per soggiorni primaverili. <strong>QUESTA È UNA EMAIL DI TEST</strong>.</p>",
            false, CampaignStatus.Draft, -5, null, null, 0, 0, Scenario.CampaignDraft),
        new("approvata", "ospiti-fedeli", "Novità TEST - Nuova struttura disponibile",
            "<p>Ciao,</p><p>Abbiamo una nuova villa sul lago disponibile per prenotazioni. <strong>QUESTA È UNA EMAIL DI TEST</strong>.</p>",
            false, CampaignStatus.Approved, -3, null, null, 0, 0, Scenario.CampaignApproved),
        new("inviata", "ospiti-fedeli", "Grazie TEST - Fine estate",
            "<p>Grazie per averci scelto quest'estate! <strong>QUESTA È UNA EMAIL DI TEST</strong>.</p>",
            true, CampaignStatus.Sent, -10, -7, -7, 42, 3, Scenario.CampaignSent),
    ];

    public static IReadOnlyList<DemoPropertyTemplate> Properties { get; } =
    [
        new(
            Key: "villa-lago",
            Name: "Villa del Lago (demo)",
            Place: new DemoPlace("Como", "013075", "LOM", "22100", "Via del Lungolago 12"),
            Description: "Annuncio di dimostrazione sul lago. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 3,
            Bathrooms: 2,
            MaxGuests: 6,
            NightlyRate: 180m,
            CleaningFee: 40m,
            Cadastral: null,
            Scenarios: [Scenario.PublishedLombardia, Scenario.BookingUpcoming, Scenario.BookingPast, Scenario.BookingManualLight],
            Bookings:
            [
                new("arrivo", "Giulia", "Bianchi", "ospite.demo.arrivo@example.com", 14, 4, BookingStatus.Confirmed, Scenario.BookingUpcoming),
                new("partito", "Marco", "Rossi", "ospite.demo.partito@example.com", -20, 4, BookingStatus.CheckedOut, Scenario.BookingPast),
                // SR-06a: entered by hand with only the name and the email, at a price agreed over the phone (the guest gives the rest with the online check-in).
                new(
                    "a-mano",
                    "Elena",
                    string.Empty,
                    "ospite.demo.a-mano@example.com",
                    40,
                    3,
                    BookingStatus.Confirmed,
                    Scenario.BookingManualLight,
                    BookingSource.Manual,
                    AgreedTotal: 500m,
                    PriceNote: "Concordato al telefono (dimostrazione)"),
            ]),
        new(
            Key: "appartamento-brera",
            Name: "Appartamento Brera (demo)",
            Place: new DemoPlace("Milano", "015146", "LOM", "20121", "Via Brera 18"),
            Description: "Annuncio di dimostrazione a Milano. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 1,
            Bathrooms: 1,
            MaxGuests: 3,
            NightlyRate: 140m,
            CleaningFee: 30m,
            Cadastral: null,
            Scenarios: [Scenario.PublishedLombardia],
            Bookings: []),
        new(
            Key: "casa-trastevere",
            Name: "Casa Trastevere (demo)",
            Place: new DemoPlace("Roma", "058091", "LAZ", "00153", "Via della Lungaretta 42"),
            Description: "Annuncio di dimostrazione a Roma. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 2,
            Bathrooms: 1,
            MaxGuests: 4,
            NightlyRate: 160m,
            CleaningFee: 35m,
            Cadastral: null,
            Scenarios: [Scenario.PublishedLazio],
            Bookings: []),
        new(
            Key: "bilocale-navigli",
            Name: "Bilocale Navigli (demo)",
            Place: new DemoPlace("Milano", "015146", "LOM", "20144", "Alzaia Naviglio Grande 14"),
            Description: "Immobile di dimostrazione in locazione lunga, con dati catastali inventati.",
            Mode: RentalMode.Long,
            Publish: false,
            Bedrooms: 1,
            Bathrooms: 1,
            MaxGuests: 0,
            NightlyRate: 0m,
            CleaningFee: 0m,
            Cadastral: new DemoCadastral("12", "345", "6", "A/2", 450m),
            Scenarios: [Scenario.LongStayCadastral],
            Bookings: []),
        new(
            Key: DraftKey,
            Name: "Monolocale in attivazione (demo)",
            Place: new DemoPlace("Como", "013075", "LOM", "22100", "Via Borgovico 4"),
            Description: "Annuncio lasciato in bozza per mostrare il percorso di attivazione. Manca il CIN, i documenti e la checklist.",
            Mode: RentalMode.Short,
            Publish: false,
            Bedrooms: 0,
            Bathrooms: 1,
            MaxGuests: 2,
            NightlyRate: 90m,
            CleaningFee: 20m,
            Cadastral: null,
            Scenarios: [Scenario.ActivationPending],
            Bookings: []),
        new(
            Key: "attico-firenze",
            Name: "Attico Firenze (demo)",
            Place: new DemoPlace("Firenze", "048017", "TOS", "50122", "Via dei Calzaiuoli 8"),
            Description: "Annuncio di dimostrazione a Firenze. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 2,
            Bathrooms: 2,
            MaxGuests: 5,
            NightlyRate: 200m,
            CleaningFee: 45m,
            Cadastral: null,
            Scenarios: [Scenario.AgencyPropertiesCrossRegion],
            Bookings:
            [
                new("airbnb-ical", "Thomas", "Schmidt", "thomas.demo@example.com", 7, 3, BookingStatus.Confirmed, Scenario.BookingOtaIcal, BookingSource.Airbnb),
            ]),
        new(
            Key: "loft-torino",
            Name: "Loft Torino (demo)",
            Place: new DemoPlace("Torino", "001272", "PIE", "10123", "Via Garibaldi 25"),
            Description: "Annuncio di dimostrazione a Torino. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 1,
            Bathrooms: 1,
            MaxGuests: 2,
            NightlyRate: 120m,
            CleaningFee: 25m,
            Cadastral: null,
            Scenarios: [Scenario.AgencyPropertiesCrossRegion],
            Bookings: []),
        new(
            Key: "villa-lugano",
            Name: "Villa Lugano (demo)",
            Place: new DemoPlace("Lugano", "999999", "TIC", "6900", "Via Nassa 15"),
            Description: "Annuncio di dimostrazione a Lugano, Ticino. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 4,
            Bathrooms: 3,
            MaxGuests: 8,
            NightlyRate: 250m,
            CleaningFee: 60m,
            Cadastral: null,
            Scenarios: [Scenario.AgencyPropertiesCrossRegion],
            Bookings: []),
        new(
            Key: "chalet-locarno",
            Name: "Chalet Locarno (demo)",
            Place: new DemoPlace("Locarno", "999998", "TIC", "6600", "Via della Motta 8"),
            Description: "Annuncio di dimostrazione a Locarno, Ticino. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 3,
            Bathrooms: 2,
            MaxGuests: 6,
            NightlyRate: 190m,
            CleaningFee: 50m,
            Cadastral: null,
            Scenarios: [Scenario.AgencyPropertiesCrossRegion],
            Bookings: []),
        new(
            Key: "appartamento-napoli",
            Name: "Appartamento Napoli (demo)",
            Place: new DemoPlace("Napoli", "063049", "CAM", "80134", "Via Toledo 156"),
            Description: "Annuncio di dimostrazione a Napoli. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 2,
            Bathrooms: 1,
            MaxGuests: 4,
            NightlyRate: 150m,
            CleaningFee: 35m,
            Cadastral: null,
            Scenarios: [Scenario.AgencyPropertiesCrossRegion],
            Bookings: []),
        new(
            Key: "villa-amalfi",
            Name: "Villa Amalfi (demo)",
            Place: new DemoPlace("Amalfi", "065006", "CAM", "84011", "Via Lorenzo d'Amalfi 3"),
            Description: "Annuncio di dimostrazione ad Amalfi. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 3,
            Bathrooms: 2,
            MaxGuests: 6,
            NightlyRate: 220m,
            CleaningFee: 55m,
            Cadastral: null,
            Scenarios: [Scenario.AgencyPropertiesCrossRegion],
            Bookings:
            [
                new("booking-com-ical", "Marie", "Dubois", "marie.demo@example.com", 21, 5, BookingStatus.Confirmed, Scenario.BookingOtaIcal, BookingSource.BookingCom),
            ]),
    ];

    /// <summary>
    /// Staff members of the agency demo: collaborators who handle guests and properties.
    /// </summary>
    public static IReadOnlyList<DemoStaffTemplate> StaffMembers { get; } =
    [
        new("sofia-conte", "Sofia", "Conte", "sofia.staff@example.com", "+39 340 1234567", OrgRole.Collaborator, Scenario.StaffCollaborator),
        new("marco-galli", "Marco", "Galli", "marco.staff@example.com", "+39 341 7654321", OrgRole.Collaborator, Scenario.StaffCollaborator),
    ];

    /// <summary>
    /// Trusted suppliers of the agency demo: one trusted supplier to demonstrate supplier management.
    /// </summary>
    public static IReadOnlyList<DemoSupplierTemplate> Suppliers { get; } =
    [
        new("pulizie-express", "Pulizie Express DEMO", "Servizio pulizie di fiducia (DEMO)", "info@pulizieexpress.example.com",
            "+39 342 9988776", true, Scenario.TrustedSupplier),
    ];

    /// <summary>
    /// Check-in verifications demo: all verification states to demonstrate the biometric and operator verification flow.
    /// No real faces - only recorded biometric outcomes.
    /// </summary>
    public static IReadOnlyList<DemoCheckInTemplate> CheckIns { get; } =
    [
        new("pending-verification", "villa-lago", "arrivo", "Pending", null, false, false, Scenario.CheckInPending),
        new("biometric-passed", "attico-firenze", "airbnb-ical", "Completed", BiometricOutcome.Accept, true, false, Scenario.CheckInBiometricPassed),
        new("operator-verified", "villa-amalfi", "booking-com-ical", "Completed", BiometricOutcome.Escalate, true, true, Scenario.CheckInOperatorVerified),
        new("rejected", "loft-torino", "rejected-guest", "Rejected", BiometricOutcome.Reject, false, false, Scenario.CheckInRejected),
    ];

    /// <summary>
    /// Alloggiati reports demo: reports in all states including SentReceiptPending with a fake receipt.
    /// </summary>
    public static IReadOnlyList<DemoAlloggiatiTemplate> AlloggiatiReports { get; } =
    [
        new("sent-receipt-pending", "villa-lago", "partito", RegistrationStatus.SentToProvider, true, false, Scenario.AlloggiatiSentReceiptPending),
        new("registered", "casa-trastevere", "past-guest-roma", RegistrationStatus.Registered, true, true, Scenario.AlloggiatiRegistered),
    ];

    /// <summary>
    /// Published owner statements demo (ADR-017): one published statement to demonstrate the statement lifecycle.
    /// </summary>
    public static IReadOnlyList<DemoStatementTemplate> Statements { get; } =
    [
        new("statement-2026-09", "mario-rossi", 9, 2026, StatementStatus.Published, 2500.00m, 375.00m, 2125.00m, Scenario.StatementPublished),
    ];

    /// <summary>
    /// Withholding summaries demo: one summary to demonstrate the withholding tax calculation.
    /// </summary>
    public static IReadOnlyList<DemoWithholdingTemplate> WithholdingSummaries { get; } =
    [
        new("withholding-2026-q3", "mario-rossi", 3, 2026, 1200.00m, 240.00m, Scenario.WithholdingSummary),
    ];

    /// <summary>
    /// F24 records demo: one F24 record to demonstrate the F24 tax payment flow.
    /// </summary>
    public static IReadOnlyList<DemoF24Template> F24Records { get; } =
    [
        new("f24-2026-09", "mario-rossi", 9, 2026, 240.00m, "1001", Scenario.F24Record),
    ];

    /// <summary>
    /// Wire batches demo: one generated batch to demonstrate the wire transfer batch lifecycle.
    /// </summary>
    public static IReadOnlyList<DemoWireBatchTemplate> WireBatches { get; } =
    [
        new("batch-2026-10", 10, 2026, WireBatchStatus.Generated, 4, 8500.00m, Scenario.WireBatchGenerated),
    ];

    /// <summary>
    /// Automation routines demo: one active and one test-mode routine to demonstrate the routine engine.
    /// </summary>
    public static IReadOnlyList<DemoRoutineTemplate> Routines { get; } =
    [
        new("welcome-message", "Messaggio di benvenuto (DEMO)", RoutineEventType.BeforeCheckin, 1, RoutineActionType.MessageToGuest, false, Scenario.RoutineActive),
        new("checkout-reminder", "Promemoria checkout (TEST)", RoutineEventType.BeforeCheckout, 1, RoutineActionType.MessageToGuest, true, Scenario.RoutineTestMode),
    ];
}

public sealed record DemoPlace(string City, string IstatCode, string RegionCode, string PostalCode, string Address);

public sealed record DemoCadastral(
    string Sheet,
    string Parcel,
    string Subaltern,
    string Category,
    decimal Income);

public sealed record DemoBookingTemplate(
    string Key,
    string FirstName,
    string LastName,
    string Email,
    int CheckInOffsetDays,
    int Nights,
    BookingStatus Status,
    string Scenario,
    BookingSource Source = BookingSource.Local,
    decimal? AgreedTotal = null,
    string? PriceNote = null);

public sealed record DemoPropertyTemplate(
    string Key,
    string Name,
    DemoPlace Place,
    string Description,
    RentalMode Mode,
    bool Publish,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    decimal NightlyRate,
    decimal CleaningFee,
    DemoCadastral? Cadastral,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<DemoBookingTemplate> Bookings);

/// <summary>State a demo host invite is created in.</summary>
public enum DemoHostInviteState
{
    Pending,
    Used,
    Expired,
}

/// <summary>An invite of the demo host: created <paramref name="CreatedOffsetDays"/> days from today (negative: in the past).</summary>
public sealed record DemoHostInviteTemplate(
    string Key,
    string Email,
    string? Message,
    int CreatedOffsetDays,
    DemoHostInviteState State,
    string Scenario);

/// <summary>
/// A property owner of the agency demo (ADR-012): fiscal code and IBAN are test data, encrypted at rest.
/// Includes ownership share percentage and mandate commission rate.
/// </summary>
public sealed record DemoPropertyOwnerTemplate(
    string Key,
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    string FiscalCode,
    string Iban,
    OwnerTaxRegime TaxRegime,
    decimal OwnershipSharePercent,
    decimal CommissionRate,
    string Scenario);

/// <summary>A guest segment for demo campaigns (NF-24): condition is empty to match all consented guests.</summary>
public sealed record DemoGuestSegmentTemplate(
    string Key,
    string Name,
    string ConditionJson,
    string Scenario);

/// <summary>A marketing campaign for demo (NF-24): created <paramref name="CreatedOffsetDays"/> days from today.</summary>
public sealed record DemoCampaignTemplate(
    string Key,
    string SegmentKey,
    string Subject,
    string Body,
    bool IsAiGenerated,
    CampaignStatus Status,
    int CreatedOffsetDays,
    int? ScheduledOffsetDays,
    int? SentOffsetDays,
    int SentCount,
    int ErrorCount,
    string Scenario);

/// <summary>An agency invoice for demo (ADR-019): one issued invoice with test amounts.</summary>
public sealed record DemoAgencyInvoiceTemplate(
    string Key,
    string OwnerKey,
    string Series,
    string InvoiceNumber,
    int Month,
    int Year,
    decimal TaxableBase,
    decimal VatRate,
    string Scenario);

/// <summary>A staff member of the agency demo: collaborator role to demonstrate staff management.</summary>
public sealed record DemoStaffTemplate(
    string Key,
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    OrgRole Role,
    string Scenario);

/// <summary>A trusted supplier of the agency demo: demonstrates supplier management and service requests.</summary>
public sealed record DemoSupplierTemplate(
    string Key,
    string Name,
    string Description,
    string Email,
    string? Phone,
    bool IsTrusted,
    string Scenario);

/// <summary>
/// A check-in verification demo: demonstrates all verification states.
/// No real faces - only recorded biometric outcomes.
/// </summary>
public sealed record DemoCheckInTemplate(
    string Key,
    string PropertyKey,
    string BookingKey,
    string VerificationStatus,
    BiometricOutcome? BiometricOutcome,
    bool HasConsent,
    bool OperatorVerified,
    string Scenario);

/// <summary>An alloggiati report demo: demonstrates report states including SentReceiptPending with fake receipt.</summary>
public sealed record DemoAlloggiatiTemplate(
    string Key,
    string PropertyKey,
    string BookingKey,
    RegistrationStatus Status,
    bool HasFakeReceipt,
    bool IsRegistered,
    string Scenario);

/// <summary>A published owner statement demo (ADR-017): demonstrates the statement lifecycle.</summary>
public sealed record DemoStatementTemplate(
    string Key,
    string OwnerKey,
    int Month,
    int Year,
    StatementStatus Status,
    decimal GrossRevenue,
    decimal Commission,
    decimal NetAmount,
    string Scenario);

/// <summary>A withholding summary demo: demonstrates the withholding tax calculation.</summary>
public sealed record DemoWithholdingTemplate(
    string Key,
    string OwnerKey,
    int Quarter,
    int Year,
    decimal TaxableBase,
    decimal WithholdingAmount,
    string Scenario);

/// <summary>An F24 record demo: demonstrates the F24 tax payment flow.</summary>
public sealed record DemoF24Template(
    string Key,
    string OwnerKey,
    int Month,
    int Year,
    decimal Amount,
    string TaxCode,
    string Scenario);

/// <summary>A wire batch demo: demonstrates the wire transfer batch lifecycle (ADR-017).</summary>
public sealed record DemoWireBatchTemplate(
    string Key,
    int Month,
    int Year,
    WireBatchStatus Status,
    int TransferCount,
    decimal TotalAmount,
    string Scenario);

/// <summary>An automation routine demo (NF-26): demonstrates active and test-mode routines.</summary>
public sealed record DemoRoutineTemplate(
    string Key,
    string Name,
    RoutineEventType EventType,
    int DaysBefore,
    RoutineActionType ActionType,
    bool IsTestMode,
    string Scenario);
