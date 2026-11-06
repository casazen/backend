using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;

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
    public const string Version = "2026-11-06.8";

    /// <summary>The body of a reset must carry this word. It is not a password: it only stops an accidental click.</summary>
    public const string ResetConfirmation = "RESET";

    public const string DraftKey = "monolocale-bozza";

    /// <summary>
    /// The email of the demo guest of the mobile app: the account the store reviewers sign in with. The bookings of
    /// <see cref="Scenario.GuestAppAccount"/> carry this address, so «Le mie prenotazioni» of the app shows them. The account itself
    /// is created by hand in Auth0, with this email and the email marked as verified (docs/runbooks/auth0.md § 12): CasaZen never creates one.
    /// </summary>
    public const string GuestAppEmail = "ospite.app.demo@example.com";

    /// <summary>
    /// Version of the invented texts the CRM guests of <see cref="Scenario.CrmProfiles"/> saw: the soft opt-in notice of the direct customer and the
    /// marketing consent of the online check-in. It says TEST, like every invented value of the showcase.
    /// </summary>
    public const string CrmTextVersion = "DEMO-TEST-1";

    /// <summary>
    /// The phone number the two guests of <see cref="Scenario.CrmProfiles"/> who share one give. Invented: seven zeros stand for what a real
    /// number would have. The other demo guests have a placeholder number (all zeros) that the CRM does not take for a phone, so they never
    /// share one.
    /// </summary>
    public const string CrmSharedPhone = "+39 333 0000777";

    /// <summary>
    /// The token of the welcome link of <see cref="Scenario.CrmWelcomeLink"/>: fixed, so a client can open <c>/benvenuto/{token}</c> after every
    /// reset. It says TEST, uses only the characters of a base64url token and is not random: a real link is 32 random characters. The token is unique
    /// among all the links of the installation, so the link is made only when no other org holds it.
    /// </summary>
    public const string CrmWelcomeToken = "TEST-benvenuto-villa-lago-demo-01";

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
        public const string PropertyWizardFields = "property-wizard-fields";
        public const string PropertySiteProfile = "property-site-profile";
        public const string GuestAppAccount = "guest-app-account";
        public const string ReportYearOverYear = "report-year-over-year";
        public const string ListingChannelStates = "listing-channel-states";
        public const string PropertyRoomsAndDiscounts = "property-rooms-and-discounts";
        public const string PopupAlerts = "popup-alerts";
        public const string WizardDrafts = "wizard-drafts";
        public const string TouristTaxPaid = "tourist-tax-paid";
        public const string MarketplacePartnersVisible = "marketplace-partners-visible";
        public const string MarketplaceActivationDeclared = "marketplace-activation-declared";
        public const string TouristTaxPerGuest = "tourist-tax-per-guest";
        public const string TouristTaxChannelCollected = "tourist-tax-channel-collected";

        /// <summary>
        /// The hub of messages (E05/F02/US01/T01, NF-61, NF-64): the conversations of the guests in one list. Three invented conversations, all saying
        /// TEST: one with the guest of a direct stay waiting for an answer, one of Airbnb about a booking, and a request of Booking.com with no booking
        /// yet. They are rows of the hub only: nothing is read from, or sent to, a channel manager.
        /// </summary>
        public const string MessageHub = "message-hub";

        // NF-40, NF-45 (E03/F01/US01/T04): the host wizard "Guest check-in" in the four states of a stay, and a stay with data to complete.
        public const string CheckInWizardExpected = "checkin-wizard-expected";
        public const string CheckInWizardArrived = "checkin-wizard-arrived";
        public const string CheckInWizardDeparted = "checkin-wizard-departed";
        public const string CheckInWizardNoShow = "checkin-wizard-no-show";
        public const string CheckInWizardIncomplete = "checkin-wizard-incomplete";

        /// <summary>
        /// Who an invoice of a booking is made out to (E06/F02/US01, FIS-01, NF-81): a finished direct stay for which the guest asked for an invoice
        /// at the online check-in and the host completed the data of the company. Written for the booking at reset, marked as showcase data
        /// (<c>TestDataEntities.BookingInvoiceData</c>) and gone with the booking.
        /// </summary>
        public const string InvoiceData = "invoice-data";

        /// <summary>
        /// Who issues the documents of the demo organization (E06/F02/US02, FIS-02, NF-82): a complete profile of the issuer, with the choice «the
        /// accountant takes care of the documents» (no flag and no connection needed), written at reset when the org has no profile of its own and
        /// deleted by the next reset (<c>TestDataEntities.FiscalIssuerProfile</c>). A real profile has no marker and is never touched.
        /// </summary>
        public const string FiscalIssuerProfile = "fiscal-issuer-profile";

        /// <summary>
        /// The CRM profiles built from the bookings (E07/F01/US02, NF-88, NF-89): a returning guest with a past and an upcoming stay at two houses, a
        /// guest whose booking comes from an OTA (the address becomes a forward, service only), a direct customer with the soft opt-in notice, a guest
        /// with a confirmed marketing consent from the online check-in, and two guests who share a phone (a merge to decide). The profiles, the
        /// contacts and the proposed merges are written by the CRM sync of the showcase bookings.
        /// </summary>
        public const string CrmProfiles = "crm-profiles";

        /// <summary>
        /// The welcome link of a house (E07/F04/US01/T03, NF-90): the address behind the QR code printed in the house, where a guest leaves a contact
        /// and a per-channel consent. The demo house has an active link with a fixed test token (<see cref="CrmWelcomeToken"/>), so the public
        /// welcome page can be opened after every reset.
        /// </summary>
        public const string CrmWelcomeLink = "crm-welcome-link";

        /// <summary>
        /// The money and the state of a booking that has a reservation on the channel manager (E08/F03/US03/T02, NF-74, NF-77): two bookings of a
        /// channel as the import leaves them (one with the amounts, who collects and whether the commission is taken out of the payments, one with only
        /// what Octorate gives today and the rest «not available»), a booking of the booking site whose mirror is synced, and a booking the host entered
        /// whose change Octorate refused (a conflict, with the dates and guests Octorate holds). The reservations, the amounts and the mirrors are
        /// invented and marked as showcase data; nothing is sent to Octorate.
        /// </summary>
        public const string BookingOtaAmounts = "booking-ota-amounts";
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
        Scenario.PropertyWizardFields,
        Scenario.PropertySiteProfile,
        Scenario.GuestAppAccount,
        Scenario.ReportYearOverYear,
        Scenario.ListingChannelStates,
        Scenario.PropertyRoomsAndDiscounts,
        Scenario.PopupAlerts,
        Scenario.WizardDrafts,
        Scenario.TouristTaxPaid,
        Scenario.MarketplacePartnersVisible,
        Scenario.MarketplaceActivationDeclared,
        Scenario.TouristTaxPerGuest,
        Scenario.TouristTaxChannelCollected,
        Scenario.MessageHub,
        Scenario.CheckInWizardExpected,
        Scenario.CheckInWizardArrived,
        Scenario.CheckInWizardDeparted,
        Scenario.CheckInWizardNoShow,
        Scenario.CheckInWizardIncomplete,
        Scenario.CrmProfiles,
        Scenario.InvoiceData,
        Scenario.FiscalIssuerProfile,
        Scenario.CrmWelcomeLink,
        Scenario.BookingOtaAmounts,
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
    /// Who owns which demo property and for how much (ADR-012-R4, NF-96): the report by owner of the agency and the owner portal weight the
    /// figures of a property by these shares. The shares of a property add up to 100: the house of the agency demo owner is his alone and
    /// has stays (it is the one with a finished stay and one a year earlier), and the villa is shared 50/50 by two owners, so the weighting
    /// shows. Owners are the ones of <see cref="PropertyOwners"/>, properties the ones of <see cref="Properties"/>, both by key.
    /// </summary>
    public static IReadOnlyList<DemoPropertyOwnershipTemplate> PropertyOwnerships { get; } =
    [
        new("mario-rossi", "casa-trastevere", 100m, Scenario.PropertyOwnerAgency),
        new("giulia-bianchi", "villa-lago", 50m, Scenario.OwnerShares),
        new("luca-verdi", "villa-lago", 50m, Scenario.OwnerShares),
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
            Scenarios: [Scenario.PublishedLombardia, Scenario.BookingUpcoming, Scenario.BookingPast, Scenario.BookingManualLight, Scenario.PropertyWizardFields, Scenario.PropertySiteProfile, Scenario.ListingChannelStates, Scenario.PropertyRoomsAndDiscounts],
            Profile: new DemoPropertyProfile(
                AccommodationType.HouseOrVilla, 5, 140, "Villa sul lago con giardino (demo)",
                new TimeOnly(15, 0), new TimeOnly(10, 0), SmokingPolicy.No, PetsPolicy.OnRequest, PartiesPolicy.No,
                new TimeOnly(22, 0), new TimeOnly(8, 0), 20m, ChildrenPolicy.Yes),
            // DB-11a: what the host says about the house on its public page: invented like the rest of the demo, no video or tour (nothing
            // of a third party is embedded from a demo).
            SiteProfile: new SitePropertyProfileInput(
                FloorLevel: 0,
                RenovatedYear: 2022,
                EnergyClass: "C",
                EnergyIndex: 112.5m,
                AreaKind: null,
                ChildrenWelcome: true,
                CheckInTo: new TimeOnly(20, 0),
                VideoUrl: null,
                TourUrl: null,
                Zone: new LocalizedTextInput("Lungolago di Como (demo)", "Como lakefront (demo)"),
                Near: new LocalizedTextInput("A cinque minuti a piedi dal lago", "Five minutes on foot from the lake"),
                FloorNote: new LocalizedTextInput("Piano terra, senza scale", "Ground floor, no stairs"),
                OutdoorNote: new LocalizedTextInput("Giardino di 300 m² con accesso al lago", "300 m² garden with lake access"),
                CheckInMethod: new LocalizedTextInput("In autonomia, con cassetta a codice", "Self check-in with a code key box"),
                RulesNote: new LocalizedTextInput("Dimostrazione: silenzio dopo le 22.", "Demo: quiet after 10 pm."),
                Highlights:
                [
                    new SiteHighlightInput("sunset", new LocalizedTextInput("Vista sul lago", "Lake view"), new LocalizedTextInput("Tramonti dal giardino.", "Sunsets from the garden.")),
                    new SiteHighlightInput("trees", new LocalizedTextInput("Giardino privato", "Private garden"), null),
                    new SiteHighlightInput("key", new LocalizedTextInput("Check-in autonomo", "Self check-in"), null),
                ],
                SafetyNotes: [new LocalizedTextInput("Rilevatore di fumo", "Smoke detector"), new LocalizedTextInput("Estintore", "Fire extinguisher")],
                Tags: [],
                // NF-31: the listing in English, written by hand (the demo translates nothing by machine), so the headline and the description
                // exist in the two languages a publication needs.
                Translations: new Dictionary<string, ListingTextsInput>
                {
                    ["en"] = new ListingTextsInput(
                        Title: "Lake villa with garden (demo)",
                        Description: "Demo listing on the lake. Data, CIN and documents are invented and marked as test."),
                }),
            Pois:
            [
                new(PoiKind.Beach, "Lido di Como (demo)", "Como lido (demo)", PoiTravelMode.Walk, 6, CompassPoint.S, 450),
                new(PoiKind.Food, "Trattoria sul lungolago (demo)", "Lakefront trattoria (demo)", PoiTravelMode.Walk, 3, CompassPoint.SE, 250),
                new(PoiKind.Sight, "Funicolare per Brunate (demo)", "Brunate funicular (demo)", PoiTravelMode.Walk, 15, CompassPoint.NE, 1300),
                new(PoiKind.Transport, "Stazione di Como San Giovanni (demo)", "Como San Giovanni station (demo)", PoiTravelMode.Car, 10, CompassPoint.W, 3200),
            ],
            // NF-27, NF-38: where the content of the house stands on three channels, one in each state the host meets most: live, to fix, and
            // written on the channel itself. Invented: the demo is connected to no channel manager and nothing here is ever sent.
            ChannelStates:
            [
                new("booking_xml", ChannelPublishStatus.Published, null, null, 3),
                new("airbnb_xml", ChannelPublishStatus.ToFix, "demo_photos_below_minimum", "Servono almeno cinque foto per questo canale (dimostrazione).", 5),
                new("expedia", ChannelPublishStatus.ManagedOnChannel, null, null, null),
            ],
            // NF-34, NF-35: the rooms of the house with their beds (three bedrooms and five beds, as the counters above say) and two
            // length-of-stay discounts: a week, and a month.
            Rooms:
            [
                new(PropertyRoomType.Bedroom, [new RoomBed(BedType.Double, 1)]),
                new(PropertyRoomType.Bedroom, [new RoomBed(BedType.Double, 1)]),
                new(PropertyRoomType.Bedroom, [new RoomBed(BedType.Single, 2)]),
                new(PropertyRoomType.LivingRoom, [new RoomBed(BedType.SofaBed, 1)]),
            ],
            StayDiscounts:
            [
                new(7, 10m),
                new(28, 20m),
            ],
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
                // The demo guest of the mobile app (the account of the store reviewers, GuestAppEmail): three stays of the public checkout
                // (Source = Direct) so «Le mie prenotazioni» of the app has an upcoming one, a past one and a cancelled one to show. Paid
                // at the property (no online payment): nothing here reaches Stripe, and cancelling from the app refunds nothing.
                new(
                    "app-in-arrivo",
                    "Anna",
                    "Demo",
                    GuestAppEmail,
                    21,
                    3,
                    BookingStatus.Confirmed,
                    Scenario.GuestAppAccount,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite),
                new(
                    "app-passata",
                    "Anna",
                    "Demo",
                    GuestAppEmail,
                    -45,
                    4,
                    BookingStatus.CheckedOut,
                    Scenario.GuestAppAccount,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite),
                new(
                    "app-annullata",
                    "Anna",
                    "Demo",
                    GuestAppEmail,
                    60,
                    2,
                    BookingStatus.Cancelled,
                    Scenario.GuestAppAccount,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    CancellationReason: BookingCancellationReason.GuestRequested),
                // NF-96: stays of the same months one year earlier (finished, three channels), so the comparison with the year before of the
                // performance report and of the trend is never empty in the demo. Invented guests at example.com, like the rest.
                new("anno-prima-diretta", "Chiara", "Neri", "ospite.demo.anno-prima-1@example.com", -351, 4, BookingStatus.CheckedOut, Scenario.ReportYearOverYear, BookingSource.Direct),
                new("anno-prima-airbnb", "Pietro", "Gallo", "ospite.demo.anno-prima-2@example.com", -344, 3, BookingStatus.CheckedOut, Scenario.ReportYearOverYear, BookingSource.Airbnb),
                new("anno-prima-booking", "Sofie", "Peeters", "ospite.demo.anno-prima-3@example.com", -325, 5, BookingStatus.CheckedOut, Scenario.ReportYearOverYear, BookingSource.BookingCom),
                // NF-46: the tourist tax of a family, guest by guest, at the official rate of Como (3,00 a night from the 14th birthday): the mother
                // pays, the father is exempt as a resident (the host's choice) and the girl of ten is exempt by age.
                new(
                    "tassa-per-ospite",
                    "Laura",
                    "Conti",
                    "ospite.demo.tassa-ospiti@example.com",
                    30,
                    3,
                    BookingStatus.Confirmed,
                    Scenario.TouristTaxPerGuest,
                    BookingSource.Direct,
                    Guests:
                    [
                        new("Laura", "Conti", StayGuestType.HeadOfFamily, 45),
                        new("Paolo", "Conti", StayGuestType.FamilyMember, 47, TouristTaxExemptionReason.ResidentCode),
                        new("Sara", "Conti", StayGuestType.FamilyMember, 10),
                    ]),
                // NF-88 (Scenario.CrmProfiles): a guest who comes back. This stay is over; the next one is at Casa Trastevere. Another record of the
                // guest, the same address: one profile, one stay counted, two houses.
                new(
                    "crm-ritorno-passato",
                    "Sofia",
                    "Marchetti",
                    "sofia.marchetti.demo@example.com",
                    -70,
                    3,
                    BookingStatus.CheckedOut,
                    Scenario.CrmProfiles,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    Phone: "+39 333 0000555"),
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
            Scenarios: [Scenario.PublishedLombardia, Scenario.TouristTaxPaid],
            Profile: new DemoPropertyProfile(
                AccommodationType.Apartment, 2, 55, null,
                new TimeOnly(14, 0), new TimeOnly(11, 0), SmokingPolicy.OutdoorsOnly, PetsPolicy.No, PartiesPolicy.No,
                new TimeOnly(22, 0), new TimeOnly(8, 0), null),
            // NF-100: three finished stays of Milano. Two carry the tourist tax the engine records (3 EUR a person a night: invented, like the rest
            // of the demo) and the third comes from a channel with none, so the summary of the comune shows an amount owed, a part of it paid
            // (TouristTaxRemittances) and a stay counted apart, never taken as a tax of zero.
            Bookings:
            [
                new("tassa-versata", "Irene", "Valli", "ospite.demo.tassa-1@example.com", -75, 3, BookingStatus.CheckedOut, Scenario.TouristTaxPaid, BookingSource.Direct, TouristTax: 18.00m),
                new("tassa-da-versare", "Omar", "Haddad", "ospite.demo.tassa-2@example.com", -50, 2, BookingStatus.CheckedOut, Scenario.TouristTaxPaid, BookingSource.Direct, TouristTax: 12.00m),
                new("tassa-senza-importo", "Katja", "Novak", "ospite.demo.tassa-3@example.com", -30, 2, BookingStatus.CheckedOut, Scenario.TouristTaxPaid, BookingSource.Airbnb),
                // NF-81 (Scenario.InvoiceData): a finished direct stay, paid at the property, for which the guest asked for an invoice at the online
                // check-in and the host completed the data of the company. Invented like the rest: a VAT number with a valid check digit, an
                // address at example.com, TEST in the name.
                new(
                    "fattura-azienda",
                    "Luca",
                    "Ferri",
                    "ospite.demo.fattura@example.com",
                    -12,
                    2,
                    BookingStatus.CheckedOut,
                    Scenario.InvoiceData,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    InvoiceData: new DemoInvoiceData(
                        InvoiceRecipientKind.Company,
                        "Demo Viaggi S.r.l. (TEST)",
                        "12345678903",
                        "IT12345678903",
                        "Via Brera",
                        "18",
                        "20121",
                        "Milano",
                        "MI",
                        "ABC1234",
                        "fatture.demo@pec.example.com",
                        InvoiceDataSource.Host)),
            ]),
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
            Scenarios: [Scenario.PublishedLazio, Scenario.AlloggiatiRegistered],
            Profile: new DemoPropertyProfile(
                AccommodationType.Apartment, 3, 70, "Casa nel cuore di Trastevere (demo)",
                new TimeOnly(15, 0), new TimeOnly(10, 30), SmokingPolicy.No, PetsPolicy.Yes, PartiesPolicy.OnRequest,
                new TimeOnly(23, 0), new TimeOnly(7, 0), 15m),
            Bookings:
            [
                new("past-guest-roma", "Francesca", "Romano", "francesca.demo@example.com", -15, 5, BookingStatus.CheckedOut, Scenario.AlloggiatiRegistered),
                // NF-96: the same months one year earlier (see the villa).
                new("anno-prima-roma", "Elisa", "Fontana", "ospite.demo.anno-prima-4@example.com", -380, 5, BookingStatus.CheckedOut, Scenario.ReportYearOverYear, BookingSource.Direct),
                // NF-40, NF-45: the check-in wizard in the four states of a stay (departed, no-show, arrived, expected) and one stay whose data
                // the host still has to complete (a birth place missing, a document expired before the arrival: a warning, never a block).
                new(
                    "checkin-partito", "Sara", "Galli", "ospite.demo.checkin-1@example.com", -7, 2, BookingStatus.CheckedOut,
                    Scenario.CheckInWizardDeparted, BookingSource.Direct,
                    Guests: [new("Sara", "Galli", StayGuestType.SingleGuest, 41)]),
                new(
                    "checkin-mancato-arrivo", "Tomas", "Lind", "ospite.demo.checkin-2@example.com", -4, 1, BookingStatus.Confirmed,
                    Scenario.CheckInWizardNoShow, BookingSource.Direct,
                    Guests: [new("Tomas", "Lind", StayGuestType.SingleGuest, 29)],
                    NoShow: true),
                new(
                    "checkin-arrivato", "Bianca", "Ferri", "ospite.demo.checkin-3@example.com", -1, 3, BookingStatus.CheckedIn,
                    Scenario.CheckInWizardArrived, BookingSource.Direct,
                    Guests:
                    [
                        new("Bianca", "Ferri", StayGuestType.HeadOfFamily, 38),
                        new("Leo", "Ferri", StayGuestType.FamilyMember, 9),
                    ]),
                new(
                    "checkin-atteso", "Nina", "Russo", "ospite.demo.checkin-4@example.com", 4, 2, BookingStatus.Confirmed,
                    Scenario.CheckInWizardExpected, BookingSource.Direct,
                    Guests:
                    [
                        new("Nina", "Russo", StayGuestType.HeadOfGroup, 45),
                        new("Ada", "Conti", StayGuestType.GroupMember, 44),
                    ]),
                new(
                    "checkin-da-completare", "Piet", "Jansen", "ospite.demo.checkin-5@example.com", 9, 2, BookingStatus.Confirmed,
                    Scenario.CheckInWizardIncomplete, BookingSource.Direct,
                    Guests:
                    [
                        new("Piet", "Jansen", StayGuestType.HeadOfFamily, 52, DocumentExpired: true),
                        new("Mila", "Jansen", StayGuestType.FamilyMember, 50, BirthPlaceMissing: true),
                    ]),
                // NF-88 (Scenario.CrmProfiles): the guest of the villa comes back, to a second house.
                new(
                    "crm-ritorno-in-arrivo",
                    "Sofia",
                    "Marchetti",
                    "sofia.marchetti.demo@example.com",
                    30,
                    3,
                    BookingStatus.Confirmed,
                    Scenario.CrmProfiles,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    Phone: "+39 333 0000555"),
                // NF-77, NF-74 (Scenario.BookingOtaAmounts): a booking of Airbnb as the import leaves it, with all the amounts of the channel and the
                // flags (the channel collects, and the portal pays net of its commission): the amounts section reads in full. Octorate gives net and
                // balance only when a documented field has them, so the sparse one at Attico Firenze shows how a missing figure reads.
                new(
                    "ota-airbnb-importata",
                    "Lena",
                    "Vogel",
                    "ospite.demo.ota-1@example.com",
                    14,
                    3,
                    BookingStatus.Confirmed,
                    Scenario.BookingOtaAmounts,
                    BookingSource.Airbnb,
                    Ota: new DemoOtaTemplate(
                        "TEST-OCT-7001",
                        Commission: 61.80m,
                        Net: 453.20m,
                        Paid: 515.00m,
                        Balance: 0.00m,
                        CompanyCollect: true,
                        CommissionIncludedInPayments: true)),
                // A booking of the booking site that CasaZen sent to Octorate: its mirror is synced, the money is the booking site's.
                new(
                    "ota-sincronizzata",
                    "Paul",
                    "Dupont",
                    "ospite.demo.ota-3@example.com",
                    20,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.BookingOtaAmounts,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    Ota: new DemoOtaTemplate(
                        "TEST-OCT-7003",
                        Sync: new DemoOctorateSyncTemplate(OctorateSyncState.Synced, OctorateSyncOperation.Create))),
                // A booking the host entered, whose change Octorate refused because the reservation changed there: a conflict, with the stay Octorate
                // holds (one day later, one guest fewer) so the host sees both versions before they choose.
                new(
                    "ota-conflitto",
                    "Irene",
                    "Sala",
                    "ospite.demo.ota-4@example.com",
                    24,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.BookingOtaAmounts,
                    BookingSource.Manual,
                    Ota: new DemoOtaTemplate(
                        "TEST-OCT-7004",
                        Sync: new DemoOctorateSyncTemplate(
                            OctorateSyncState.Conflict,
                            OctorateSyncOperation.Update,
                            Services.OctorateSyncErrorCodes.ReservationChanged,
                            VersionCheckInShiftDays: 1,
                            VersionGuests: 1))),
            ]),
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
            Profile: new DemoPropertyProfile(
                AccommodationType.RoomOrSuite, 1, 28, null,
                null, null, null, null, null,
                null, null, null),
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
                // NF-96: the same months one year earlier (see the villa).
                new("anno-prima-firenze", "Hugo", "Martin", "ospite.demo.anno-prima-5@example.com", -358, 3, BookingStatus.CheckedOut, Scenario.ReportYearOverYear, BookingSource.Airbnb),
                // NF-88, NF-89 (Scenario.CrmProfiles): a booking that comes from an OTA. Its address is a forward: the contact is service only and
                // never marketable, and it is no key to recognise the guest by.
                new(
                    "crm-prenotazione-ota",
                    "Hans",
                    "Weber",
                    "hans.weber.demo@example.com",
                    20,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.CrmProfiles,
                    BookingSource.BookingCom),
                // NF-77 (Scenario.BookingOtaAmounts): a booking of Booking.com with only what Octorate gives today, the amount paid. The commission, the
                // net and the balance are «not available», who collects is not said, and Booking.com invoices its commission apart.
                new(
                    "ota-booking-sparsa",
                    "Marta",
                    "Kowalska",
                    "ospite.demo.ota-2@example.com",
                    30,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.BookingOtaAmounts,
                    BookingSource.BookingCom,
                    Ota: new DemoOtaTemplate("TEST-OCT-7002", Paid: 100.00m, CommissionIncludedInPayments: false)),
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
            Scenarios: [Scenario.AgencyPropertiesCrossRegion, Scenario.CheckInRejected],
            Bookings:
            [
                new("rejected-guest", "Paolo", "Neri", "paolo.demo@example.com", -5, 2, BookingStatus.CheckedOut, Scenario.CheckInRejected),
                // NF-88, NF-89 (Scenario.CrmProfiles): a customer of the public checkout who was shown the soft opt-in notice and whose booking is
                // confirmed: the contact is on soft opt-in (art. 130 c. 4 Codice privacy), marketable with the unsubscribe in every message.
                new(
                    "crm-cliente-diretto",
                    "Chiara",
                    "Fontana",
                    "chiara.fontana.demo@example.com",
                    10,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.CrmProfiles,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    SoftOptInNotice: true),
            ]),
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
            // NF-88 (Scenario.CrmProfiles): two different guests who left the same phone number (a family, an agent who books for others). They
            // are two profiles; the matching proposes the merge to a person and never does it.
            Bookings:
            [
                new(
                    "crm-telefono-condiviso-1",
                    "Elisa",
                    "Moretti",
                    "elisa.moretti.demo@example.com",
                    -40,
                    3,
                    BookingStatus.CheckedOut,
                    Scenario.CrmProfiles,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    Phone: CrmSharedPhone),
                new(
                    "crm-telefono-condiviso-2",
                    "Paolo",
                    "Riva",
                    "paolo.riva.demo@example.com",
                    25,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.CrmProfiles,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    Phone: CrmSharedPhone),
            ]),
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
                // NF-96: the same months one year earlier (see the villa).
                new("anno-prima-amalfi", "Lucia", "Esposito", "ospite.demo.anno-prima-6@example.com", -344, 5, BookingStatus.CheckedOut, Scenario.ReportYearOverYear, BookingSource.BookingCom),
                // NF-88, NF-89, NF-90 (Scenario.CrmProfiles): a guest who ticked the marketing consent in the online check-in and confirmed the
                // address (double opt-in): the contact is a consent, confirmed, marketable.
                new(
                    "crm-consenso-checkin",
                    "Marco",
                    "Pellegrini",
                    "marco.pellegrini.demo@example.com",
                    12,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.CrmProfiles,
                    BookingSource.Direct,
                    PaymentOption: PaymentOption.OnSite,
                    CheckInMarketingConsent: true),
            ]),
        // NF-46, ADR-037-R3: a house in an invented comune (ISTAT 999990, a code no real comune has, so the demo channel rule below touches
        // no stay of a real comune). The comune has no official rate: the demo host declared one (with an invented source), and an Airbnb stay
        // there is collected by the channel, so the host has nothing to collect while the amounts per guest are kept.
        new(
            Key: "casa-borgo-prova",
            Name: "Casa Borgo di Prova (demo)",
            Place: new DemoPlace("Borgo di Prova (TEST)", "999990", "LOM", "20000", "Via di Prova 1"),
            Description: "Casa di dimostrazione in un comune inventato, per l'imposta di soggiorno riscossa dal canale. Tutto è segnato come test.",
            Mode: RentalMode.Short,
            Publish: false,
            Bedrooms: 1,
            Bathrooms: 1,
            MaxGuests: 2,
            NightlyRate: 95m,
            CleaningFee: 25m,
            Cadastral: null,
            Scenarios: [Scenario.TouristTaxChannelCollected],
            Bookings:
            [
                new(
                    "canale-riscuote",
                    "Jonas",
                    "Berg",
                    "ospite.demo.canale@example.com",
                    45,
                    2,
                    BookingStatus.Confirmed,
                    Scenario.TouristTaxChannelCollected,
                    BookingSource.Airbnb,
                    Guests:
                    [
                        new("Jonas", "Berg", StayGuestType.HeadOfGroup, 34),
                        new("Emma", "Berg", StayGuestType.GroupMember, 33),
                    ]),
            ],
            DeclaredTouristTaxRate: new DemoDeclaredTouristTaxRate(
                2.00m,
                MaxNights: 5,
                MinimumAge: 14,
                SourceNote: "Delibera di prova del comune (TEST): importo inventato per la dimostrazione.")),
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

    /// <summary>
    /// Popup alerts of the demo host (NF-08, ADR-025): one open and one snoozed, so the queue of the host shows an alert at once and has
    /// another one coming back later. They are written for the owner of the org at reset, point at the demo stays and houses by key
    /// (<see cref="DemoPopupAlertTemplate.PropertyKey"/>, <see cref="DemoPopupAlertTemplate.BookingKey"/>) and carry no text and no
    /// personal data: a type, a reference and an urgency, like every alert.
    /// </summary>
    public static IReadOnlyList<DemoPopupAlertTemplate> PopupAlerts { get; } =
    [
        // Open: the check-in of the stay that arrives in two weeks is still to do. Pending as soon as the host opens the app.
        new("check-in-da-completare", AlertType.CheckInPending, "villa-lago", "arrivo", AlertUrgency.High, null, Scenario.PopupAlerts),
        // Snoozed: the host chose «Remind me later» on the house still in draft; the alert comes back in three hours.
        new("bozza-da-completare", AlertType.ComplianceIssue, DraftKey, null, AlertUrgency.Normal, 3, Scenario.PopupAlerts),
    ];

    /// <summary>
    /// Wizard drafts demo (ADR-024): one half-filled draft of the demo host (a manual booking left at the guest step), so that the screens
    /// that resume a draft have one to show. The typed data is invented (the guest is <c>@example.com</c> and the name says TEST); the
    /// reset splits it as the service does, so the guest's name, e-mail and phone are stored encrypted and the rest in clear. Saved two days
    /// ago: inside the 30 days a draft is kept, so the retention does not take it the night after a reset.
    /// </summary>
    public static IReadOnlyList<DemoWizardDraftTemplate> WizardDrafts { get; } =
    [
        new(
            "prenotazione-a-meta",
            WizardType.BookingCreate,
            """{"step":"ospite","nights":3,"guestCount":2,"guestName":"Ospite Demo (TEST)","guestEmail":"ospite.demo.bozza@example.com","guestPhone":"+390000000000","notes":"Bozza di dimostrazione: prenotazione a mano lasciata a metà."}""",
            -2,
            Scenario.WizardDrafts),
    ];

    /// <summary>
    /// Tourist tax payments of the demo host (NF-100): one payment to Milano, with a receipt (an invented one-page PDF that says it is test
    /// data), for the first of the three stays of <see cref="Scenario.TouristTaxPaid"/> in Appartamento Brera: 18.00 EUR, the tax recorded on it,
    /// for the days from 90 to 60 before the reset, made 55 days before it. The second stay (12.00 EUR) is not paid, so the summary of the
    /// comune over the last three months shows 30.00 owed, 18.00 paid, 12.00 to pay, a payment with its receipt and one stay with no amount.
    /// </summary>
    public static IReadOnlyList<DemoTouristTaxRemittanceTemplate> TouristTaxRemittances { get; } =
    [
        new(
            "versamento-milano",
            "appartamento-brera",
            -90,
            -60,
            18.00m,
            -55,
            "Bonifico di prova (TEST)",
            "Versamento di dimostrazione con ricevuta inventata (TEST).",
            Scenario.TouristTaxPaid),
    ];

    /// <summary>
    /// Partners of the marketplace catalog (E09/F01, NF-54, NF-59): one partner that is obviously test data (TEST in its name, an example address,
    /// texts in Italian and English that say so), visible to every plan and country, so the Partners group of a test environment is not empty. It is
    /// platform data like every partner, entered by the showcase because the admin screens that enter one are not built yet; the showcase creates it
    /// only if the catalog has no partner with that code, and the reset of the org that created it deletes it.
    /// </summary>
    public static IReadOnlyList<DemoMarketplacePartnerTemplate> MarketplacePartners { get; } =
    [
        new(
            "partner-di-prova",
            "partner-di-prova-test",
            "Partner di prova (TEST)",
            MarketplacePartnerJob.Cleaning,
            0,
            "https://example.com/",
            MarketplaceCostInvoicer.Partner,
            [Casazen.Core.Suppliers.ServiceCategories.Cleaning],
            new DemoMarketplacePartnerText(
                "Una scheda di prova per vedere come appare un partner (TEST).",
                "Per chi vuole vedere un esempio di scheda del marketplace (TEST).",
                "Scheda di dimostrazione con dati inventati: non è un servizio reale e non porta a un sito vero (TEST).",
                "Nessun requisito: è una scheda di prova.\nIl sito è un indirizzo di esempio.",
                "Nessun costo reale: scheda di prova (TEST).",
                "Nessun dato viene condiviso: è una scheda di prova (TEST).",
                "Per disattivarlo basta dichiarare di averlo spento (TEST).",
                "Apri il sito di esempio e torna qui: è una guida di prova (TEST).",
                [new DemoMarketplaceFaq("Questa scheda è reale?", "No: è una scheda di dimostrazione con dati inventati (TEST).")]),
            new DemoMarketplacePartnerText(
                "A test card to see how a partner looks (TEST).",
                "For anyone who wants to see an example of a marketplace card (TEST).",
                "Demonstration card with invented data: it is not a real service and does not lead to a real site (TEST).",
                "No requirements: it is a test card.\nThe site is an example address.",
                "No real cost: test card (TEST).",
                "No data is shared: it is a test card (TEST).",
                "To turn it off it is enough to declare that you switched it off (TEST).",
                "Open the example site and come back here: this is a test guide (TEST).",
                [new DemoMarketplaceFaq("Is this card real?", "No: it is a demonstration card with invented data (TEST).")]),
            Scenario.MarketplacePartnersVisible),
    ];

    /// <summary>
    /// What the demo host declared activated on the marketplace (E09/F01, NF-56): the partner of <see cref="MarketplacePartners"/>, activated on its own
    /// site and declared by the host a few days ago, so the Active tab shows an integration whose status the host declared and Octorate did not prove.
    /// Written for the org at reset, marked as showcase data (<c>TestDataEntities.MarketplaceActivation</c>), deleted by the next reset.
    /// </summary>
    public static IReadOnlyList<DemoMarketplaceActivationTemplate> MarketplaceActivations { get; } =
    [
        new("attivazione-dichiarata", "partner-di-prova", -3, Scenario.MarketplaceActivationDeclared),
    ];

    /// <summary>
    /// The conversations of the hub of messages (E05/F02/US01/T01, NF-61, NF-64), written for the org at reset next to the stays they are about:
    /// the guest of a direct stay of the villa who asks to arrive early and has had no answer (a conversation to answer, one message unread); a guest
    /// of Airbnb about the stay at Casa Borgo di Prova, who got an answer and wrote again (a conversation of a channel, linked to its booking); and a
    /// request of Booking.com for the villa, with no booking yet. Everything in them is invented and says TEST; the conversations of the channels are
    /// rows of the copy that the sync would write (<c>OtaMessageThread</c>, <c>OtaMessage</c>), with no connection to a channel manager behind them.
    /// Marked as showcase data (<c>TestDataEntities.OtaMessageThread</c>, <c>GuestThread</c>), deleted by the next reset.
    /// </summary>
    public static IReadOnlyList<DemoMessageThreadTemplate> MessageThreads { get; } =
    [
        new(
            "diretta-arrivo-anticipato",
            DemoMessageThreadKind.Direct,
            "villa-lago",
            "tassa-per-ospite",
            null,
            null,
            [
                new(true, 190, "Buongiorno (TEST): possiamo arrivare un’ora prima del check-in? Grazie."),
            ],
            Scenario.MessageHub),
        new(
            "airbnb-prenotazione",
            DemoMessageThreadKind.OtaReservation,
            "casa-borgo-prova",
            "canale-riscuote",
            OtaChannel.Airbnb,
            "Jonas B. (TEST)",
            [
                new(true, 2900, "Hello (TEST)! Is there a parking space near the house?", Read: true),
                new(false, 2800, "Hello Jonas (TEST), yes: there is free parking right in front of the house."),
                new(true, 75, "Perfect, thank you (TEST)! What time can we check in?"),
            ],
            Scenario.MessageHub),
        new(
            "booking-richiesta",
            DemoMessageThreadKind.OtaRequest,
            "villa-lago",
            null,
            OtaChannel.Booking,
            "Marta R. (TEST)",
            [
                new(true, 1500, "Buonasera (TEST): la villa è libera dal 3 al 10 agosto? Siamo in quattro."),
            ],
            Scenario.MessageHub),
    ];

    /// <summary>
    /// The profile of the issuer of the documents of the demo organization (E06/F02/US02, FIS-02, NF-82): complete, so the wizard «Come emetti i
    /// documenti?» opens on an answered setting and the settings page has data to show. Invented like the rest (TEST in the name, a plausible address
    /// in Milano), with the choice «the accountant takes care of the documents»: it needs neither the flag of the issuance nor an Octorate connection.
    /// Written for the org at reset only when the org has no profile (there is one per org, and a real one is never touched), marked as showcase data
    /// (<c>TestDataEntities.FiscalIssuerProfile</c>) and deleted by the next reset.
    /// </summary>
    public static IReadOnlyList<DemoFiscalIssuerProfileTemplate> FiscalIssuerProfiles { get; } =
    [
        new(
            "emittente-demo",
            DocumentIssuerMode.Accountant,
            DocumentRegime.VatOrdinary,
            "Casa Demo S.r.l. (TEST)",
            "Via Brera",
            "18",
            "20121",
            "Milano",
            "MI",
            "RF01",
            -5,
            Scenario.FiscalIssuerProfile),
    ];

    /// <summary>
    /// The channel rules of the tourist tax (NF-46, ADR-037-R3): platform data like every rule, so the showcase writes only the one of the
    /// invented comune of <c>casa-borgo-prova</c> (ISTAT 999990, which no real comune has: no real stay is ever touched), with an example source
    /// and a note that says it is test data. Created only when no rule exists for the channel and the comune, deleted by the reset of the org
    /// that created it.
    /// </summary>
    public static IReadOnlyList<DemoTouristTaxChannelRuleTemplate> TouristTaxChannelRules { get; } =
    [
        new(
            "airbnb-borgo-prova",
            "casa-borgo-prova",
            BookingSource.Airbnb,
            TouristTaxCollector.Channel,
            "https://example.com/tassa-di-soggiorno-borgo-di-prova",
            "Regola di prova (TEST): fonte inventata, il comune non esiste.",
            Scenario.TouristTaxChannelCollected),
    ];

    /// <summary>
    /// The welcome link of the demo house (E07/F04/US01/T03, NF-90): the lake villa has an active link with the fixed test token
    /// <see cref="CrmWelcomeToken"/>, so the host sees the QR code of a house that has one and a client can open the public welcome page. Written
    /// for the org at reset, marked as showcase data (<c>TestDataEntities.WelcomeLink</c>), deleted with its house by the next reset.
    /// </summary>
    public static IReadOnlyList<DemoWelcomeLinkTemplate> WelcomeLinks { get; } =
    [
        new("benvenuto-villa-lago", "villa-lago", CrmWelcomeToken, Scenario.CrmWelcomeLink),
    ];
}

/// <summary>
/// The profile of the issuer of the documents of the demo organization (FIS-02): the answers of the wizard «Come emetti i documenti?», invented.
/// <paramref name="ConfirmedOffsetDays"/> is when the wizard was completed, in days from the reset (negative: in the past).
/// </summary>
public sealed record DemoFiscalIssuerProfileTemplate(
    string Key,
    DocumentIssuerMode Mode,
    DocumentRegime Regime,
    string LegalName,
    string AddressStreet,
    string AddressCivic,
    string PostalCode,
    string City,
    string Province,
    string FatturaPaRegimeCode,
    int ConfirmedOffsetDays,
    string Scenario);

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
    string? PriceNote = null,
    PaymentOption PaymentOption = PaymentOption.Immediate,
    BookingCancellationReason? CancellationReason = null,
    decimal? TouristTax = null,
    IReadOnlyList<DemoStayGuestTemplate>? Guests = null,
    bool NoShow = false,
    string? Phone = null,
    bool SoftOptInNotice = false,
    bool CheckInMarketingConsent = false,
    DemoOtaTemplate? Ota = null,
    DemoInvoiceData? InvoiceData = null);

/// <summary>
/// The reservation of a demo stay on the channel manager (E08/F03/US03/T02, NF-74, NF-77): invented like the rest of the demo, nothing is sent to
/// Octorate. The booking carries the reservation id (<c>TEST-OCT-…</c>, unique in the org) and, for a booking of a channel, the amounts and the flags the
/// import would have stored; a booking CasaZen holds has no amount, and its mirror is <paramref name="Sync"/>. A figure left out is «not available».
/// </summary>
/// <param name="ReservationId">The id of the reservation on Octorate; it says TEST and is at most 50 characters.</param>
/// <param name="Commission">What the channel kept.</param>
/// <param name="Net">What the host receives.</param>
/// <param name="Paid">What the guest paid.</param>
/// <param name="Balance">What is still to pay.</param>
/// <param name="CompanyCollect">Who collects: <c>true</c> the channel, <c>false</c> the host, <c>null</c> not said.</param>
/// <param name="CommissionIncludedInPayments">Whether the portal pays the host net of its commission; <c>null</c> not known.</param>
/// <param name="Sync">The mirror of the booking on Octorate (a row of <c>OctorateBookingSync</c>); <c>null</c> for a booking that has none.</param>
public sealed record DemoOtaTemplate(
    string ReservationId,
    decimal? Commission = null,
    decimal? Net = null,
    decimal? Paid = null,
    decimal? Balance = null,
    bool? CompanyCollect = null,
    bool? CommissionIncludedInPayments = null,
    DemoOctorateSyncTemplate? Sync = null);

/// <summary>
/// The mirror of a demo booking on Octorate: the state of the last operation sent. For a conflict, the stay Octorate holds is the booking's own with the
/// arrival moved by <paramref name="VersionCheckInShiftDays"/> days (the departure with it) and <paramref name="VersionGuests"/> guests.
/// </summary>
/// <param name="State">Where the mirror stands.</param>
/// <param name="LastOperation">The last operation sent.</param>
/// <param name="ErrorCode">A code of <c>OctorateSyncErrorCodes</c> when the operation failed or was refused.</param>
/// <param name="VersionCheckInShiftDays">Days between the arrival of the booking and the one Octorate holds (a conflict only).</param>
/// <param name="VersionGuests">The guests Octorate holds (a conflict only).</param>
public sealed record DemoOctorateSyncTemplate(
    OctorateSyncState State,
    OctorateSyncOperation LastOperation,
    string? ErrorCode = null,
    int? VersionCheckInShiftDays = null,
    int? VersionGuests = null);

/// <summary>
/// Who an invoice of a demo stay is made out to (NF-81): the invoice data written for the booking at reset, the guest's «yes» at the check-in included.
/// Invented: a VAT number with a valid check digit, an address and a PEC at example.com, TEST in the name.
/// </summary>
public sealed record DemoInvoiceData(
    InvoiceRecipientKind Kind,
    string Name,
    string? FiscalCode,
    string? VatNumber,
    string AddressStreet,
    string AddressCivic,
    string PostalCode,
    string City,
    string Province,
    string? SdiRecipientCode,
    string? Pec,
    InvoiceDataSource Source);

/// <summary>
/// A registered guest of a demo stay (NF-40, NF-46): invented like the rest of the demo, with complete Alloggiati data of the right shape (an
/// Italian identity card for a single guest or a head, the document number says TEST). The date of birth is set so the guest has
/// <paramref name="AgeAtCheckIn"/> years on the arrival day.
/// </summary>
/// <param name="FirstName">First name.</param>
/// <param name="LastName">Last name.</param>
/// <param name="Type">Kind of guest of the Alloggiati record.</param>
/// <param name="AgeAtCheckIn">Age on the check-in day.</param>
/// <param name="TaxExemptionCode">The tourist tax exemption the host chose (a non-automatic reason), if any.</param>
/// <param name="DocumentExpired">The document of the guest expired before the arrival (the check-in wizard warns, never blocks).</param>
/// <param name="BirthPlaceMissing">The comune of birth is not entered yet (the check-in wizard asks for it).</param>
public sealed record DemoStayGuestTemplate(
    string FirstName,
    string LastName,
    StayGuestType Type,
    int AgeAtCheckIn,
    string? TaxExemptionCode = null,
    bool DocumentExpired = false,
    bool BirthPlaceMissing = false);

/// <summary>
/// The rate the demo host declared for the comune of a demo house with no official one (NF-46, AC-NF46-6): a fixed amount per person per night,
/// with an invented source note that says so. Tenant data of the demo org, deleted by the reset.
/// </summary>
public sealed record DemoDeclaredTouristTaxRate(
    decimal RatePerPersonPerNight,
    int? MaxNights,
    int MinimumAge,
    string SourceNote);

/// <summary>
/// A channel rule of the tourist tax written by the showcase (NF-46, ADR-037-R3): platform data, for the comune of the demo house
/// <paramref name="PropertyKey"/> only.
/// </summary>
public sealed record DemoTouristTaxChannelRuleTemplate(
    string Key,
    string PropertyKey,
    BookingSource Channel,
    TouristTaxCollector Collector,
    string SourceUrl,
    string Notes,
    string Scenario);

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
    IReadOnlyList<DemoBookingTemplate> Bookings,
    DemoPropertyProfile? Profile = null,
    SitePropertyProfileInput? SiteProfile = null,
    IReadOnlyList<DemoPoi>? Pois = null,
    IReadOnlyList<DemoChannelState>? ChannelStates = null,
    IReadOnlyList<DemoRoom>? Rooms = null,
    IReadOnlyList<DemoStayDiscount>? StayDiscounts = null,
    DemoDeclaredTouristTaxRate? DeclaredTouristTaxRate = null);

/// <summary>
/// A room of a demo property with its beds (NF-34): invented like the rest of the demo. The counters of the property (<c>Bedrooms</c>, <c>Beds</c>)
/// are those the rooms give, as the rooms service sets them.
/// </summary>
public sealed record DemoRoom(PropertyRoomType Type, IReadOnlyList<RoomBed> Beds);

/// <summary>A length-of-stay discount of a demo property (NF-35): from <paramref name="MinNights"/> nights, <paramref name="PercentOff"/> percent less on the lodging.</summary>
public sealed record DemoStayDiscount(int MinNights, decimal PercentOff);

/// <summary>
/// The state of a demo property on one channel of the channel manager (NF-27, NF-38): invented like the rest of the demo. It exists so the list
/// of the channels of the host is not empty and shows each state; nothing is sent, a demo property is connected to no channel manager.
/// </summary>
/// <param name="ChannelCode">The code of the portal, as <see cref="ListingChannelState.ChannelCode"/> keeps it.</param>
/// <param name="Status">Where the content stands on the channel.</param>
/// <param name="ErrorCode">A stable code of what is wrong (only for <see cref="ChannelPublishStatus.ToFix"/>).</param>
/// <param name="ErrorMessage">What is wrong, in simple words (only for <see cref="ChannelPublishStatus.ToFix"/>).</param>
/// <param name="LastSyncedHoursAgo">How many hours before the reset the channel last answered; <c>null</c> = never.</param>
public sealed record DemoChannelState(
    string ChannelCode,
    ChannelPublishStatus Status,
    string? ErrorCode,
    string? ErrorMessage,
    int? LastSyncedHoursAgo);

/// <summary>
/// A point of interest of a demo property (DB-11a): invented like the rest of the demo. The distance is stored through the same rounding as a host's
/// (<see cref="PoiDistance"/>), so a demo can never hold a figure a host could not.
/// </summary>
public sealed record DemoPoi(PoiKind Kind, string NameIt, string NameEn, PoiTravelMode Mode, int Minutes, CompassPoint Direction, int DistanceMeters);

/// <summary>
/// The fields of the wizard «Aggiungi immobile» (SR-07a) a demo property carries, so the list and the cards of the host are not
/// empty. Invented like the rest of the demo; a property without a profile keeps them unset, as every real property that existed
/// before the wizard. The rules are consistent with the house rules text of the demo (no smoking, quiet after 22:00).
/// </summary>
public sealed record DemoPropertyProfile(
    AccommodationType Type,
    int Beds,
    int AreaSqm,
    string? ListingTitle,
    TimeOnly? CheckInFrom,
    TimeOnly? CheckOutBy,
    SmokingPolicy? Smoking,
    PetsPolicy? Pets,
    PartiesPolicy? Parties,
    TimeOnly? QuietHoursFrom,
    TimeOnly? QuietHoursTo,
    decimal? ExtraGuestFeePerNight,
    ChildrenPolicy? Children = null);

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

/// <summary>
/// The share of a demo property an owner holds (ADR-012-R4): <paramref name="SharePercent"/> of <paramref name="PropertyKey"/>, from the
/// start of the showcase and without an end. The owner is a <see cref="DemoPropertyOwnerTemplate"/> by key.
/// </summary>
public sealed record DemoPropertyOwnershipTemplate(string OwnerKey, string PropertyKey, decimal SharePercent, string Scenario);

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

/// <summary>Which of the two sources of the hub of messages a demo conversation is kept in, and what it is about.</summary>
public enum DemoMessageThreadKind
{
    /// <summary>The conversation with the guest of a direct stay (<c>GuestThread</c>).</summary>
    Direct = 0,

    /// <summary>A conversation of a channel about a booking (<c>OtaMessageThread</c> of kind Reservation, linked to the booking).</summary>
    OtaReservation = 1,

    /// <summary>A conversation of a channel with no booking yet (<c>OtaMessageThread</c> of kind Request).</summary>
    OtaRequest = 2,
}

/// <summary>
/// A conversation of the hub of messages (E05/F02/US01/T01, NF-61, NF-64): invented like the rest of the demo, written for the org at reset next
/// to the stay it is about and marked as showcase data (<c>TestDataEntities.OtaMessageThread</c> or <c>GuestThread</c>), so the next reset deletes it.
/// </summary>
/// <param name="Key">Stable key of the conversation inside the template.</param>
/// <param name="Kind">Where it is kept and what it is about.</param>
/// <param name="PropertyKey">The demo property (<see cref="DemoPropertyTemplate.Key"/>) it belongs to.</param>
/// <param name="BookingKey">The demo stay (<see cref="DemoBookingTemplate.Key"/>) of that property it is about; <c>null</c> for a request.</param>
/// <param name="Channel">The channel of a conversation of a channel; <c>null</c> for a direct one.</param>
/// <param name="GuestName">The name the channel gives for the guest of a conversation of a channel (it says TEST); <c>null</c> for a direct one, which takes the guest of the stay.</param>
/// <param name="Messages">The messages, in the order they were written.</param>
/// <param name="Scenario">The scenario it exercises.</param>
public sealed record DemoMessageThreadTemplate(
    string Key,
    DemoMessageThreadKind Kind,
    string PropertyKey,
    string? BookingKey,
    OtaChannel? Channel,
    string? GuestName,
    IReadOnlyList<DemoMessageTemplate> Messages,
    string Scenario);

/// <summary>A message of a demo conversation: who wrote it, how long before the reset, what it says (it says TEST) and whether the host read it.</summary>
public sealed record DemoMessageTemplate(bool FromGuest, int MinutesAgo, string Text, bool Read = false);

/// <summary>
/// A popup alert of the demo host (NF-08, ADR-025): invented like the rest of the demo, written for the owner of the org at reset and
/// marked as showcase data (<c>TestDataEntities.PopupAlert</c>), so the next reset deletes it. It names what it is about by key: the demo
/// stay <paramref name="BookingKey"/> of the house <paramref name="PropertyKey"/>, or the house itself when there is no booking key.
/// </summary>
/// <param name="Key">Stable key of the alert inside the template.</param>
/// <param name="Type">What the alert is about.</param>
/// <param name="PropertyKey">The demo property (<see cref="DemoPropertyTemplate.Key"/>) the alert is about, or the one of the stay.</param>
/// <param name="BookingKey">The demo stay (<see cref="DemoBookingTemplate.Key"/>) of that property; <c>null</c> = the alert is about the property.</param>
/// <param name="Urgency">How urgent it is.</param>
/// <param name="SnoozedForHours">Hours from the reset until the alert is pending again (it was snoozed); <c>null</c> = open, pending at once.</param>
/// <param name="Scenario">The scenario it exercises.</param>
public sealed record DemoPopupAlertTemplate(
    string Key,
    AlertType Type,
    string PropertyKey,
    string? BookingKey,
    AlertUrgency Urgency,
    int? SnoozedForHours,
    string Scenario);

/// <summary>
/// A saved draft of the demo host (ADR-024): <paramref name="DataJson"/> is what the person typed in the wizard, a JSON object with
/// the personal fields still in it (the seeding splits them off, as the service does). Saved <paramref name="SavedOffsetDays"/> days
/// from today (negative: in the past, inside the 30 days a draft is kept). The demo host has one draft per wizard of a new entity.
/// </summary>
public sealed record DemoWizardDraftTemplate(
    string Key,
    WizardType Type,
    string DataJson,
    int SavedOffsetDays,
    string Scenario);

/// <summary>
/// A payment of the tourist tax of the demo host (NF-100): invented like the rest of the demo. It pays the comune of the demo house
/// <paramref name="PropertyKey"/> for the days from <paramref name="PeriodFromOffsetDays"/> to <paramref name="PeriodToOffsetDays"/> from the
/// reset (negative: in the past), was made <paramref name="PaidOnOffsetDays"/> days from it (never after today) and comes with a receipt,
/// an invented one-page PDF that says it is test data. Written for the org at reset and marked as showcase data
/// (<c>TestDataEntities.TouristTaxRemittance</c>), so the next reset deletes it and its file.
/// </summary>
/// <param name="Key">Stable key of the payment inside the template.</param>
/// <param name="PropertyKey">The demo property (<see cref="DemoPropertyTemplate.Key"/>) whose comune is paid.</param>
/// <param name="PeriodFromOffsetDays">First day of the period the payment pays, in days from the reset.</param>
/// <param name="PeriodToOffsetDays">Last day of that period, included, in days from the reset.</param>
/// <param name="PaidAmount">Euros, above zero, to the cent.</param>
/// <param name="PaidOnOffsetDays">The day it was paid, in days from the reset: today or earlier.</param>
/// <param name="Reference">The reference the host wrote.</param>
/// <param name="Notes">The note of the host.</param>
/// <param name="Scenario">The scenario it exercises.</param>
public sealed record DemoTouristTaxRemittanceTemplate(
    string Key,
    string PropertyKey,
    int PeriodFromOffsetDays,
    int PeriodToOffsetDays,
    decimal PaidAmount,
    int PaidOnOffsetDays,
    string Reference,
    string Notes,
    string Scenario);

/// <summary>
/// A partner of the marketplace catalog (E09/F01): platform data like every partner, marked as showcase data by the org whose reset created it
/// (<c>TestDataEntities.MarketplacePartner</c>). <paramref name="Code"/> is the lowercase slug the activations name it by.
/// </summary>
/// <param name="Key">Stable key of the partner inside the template.</param>
/// <param name="Code">The slug of the partner (unique in the catalog).</param>
/// <param name="Name">The name; it says TEST.</param>
/// <param name="Job">The work it does, which orders the Partners group.</param>
/// <param name="SortOrder">The place among the partners of the same job.</param>
/// <param name="WebsiteUrl">An example address (https).</param>
/// <param name="InvoicedBy">Who invoices the cost.</param>
/// <param name="ServiceCategories">The codes of <c>GET api/service-categories</c> it works for.</param>
/// <param name="Italian">The Italian texts.</param>
/// <param name="English">The English texts.</param>
/// <param name="Scenario">The scenario it exercises.</param>
public sealed record DemoMarketplacePartnerTemplate(
    string Key,
    string Code,
    string Name,
    MarketplacePartnerJob Job,
    int SortOrder,
    string WebsiteUrl,
    MarketplaceCostInvoicer InvoicedBy,
    IReadOnlyList<string> ServiceCategories,
    DemoMarketplacePartnerText Italian,
    DemoMarketplacePartnerText English,
    string Scenario);

/// <summary>The words of a showcase partner in one language; the lines of <paramref name="Requirements"/> and <paramref name="DataShared"/> are separated by a line break.</summary>
public sealed record DemoMarketplacePartnerText(
    string Benefit,
    string Audience,
    string Description,
    string Requirements,
    string? CostText,
    string DataShared,
    string Deactivation,
    string ActivationGuide,
    IReadOnlyList<DemoMarketplaceFaq> Faq);

/// <summary>A question and its answer.</summary>
public sealed record DemoMarketplaceFaq(string Question, string Answer);

/// <summary>
/// The partner the demo host declared activated (E09/F01, NF-56): an org-level activation of <paramref name="PartnerKey"/>, status active and
/// declared (not proven by Octorate), declared <paramref name="DeclaredOffsetDays"/> days from the reset (negative: in the past).
/// </summary>
public sealed record DemoMarketplaceActivationTemplate(string Key, string PartnerKey, int DeclaredOffsetDays, string Scenario);

/// <summary>
/// The active welcome link of a demo house (E07/F04/US01/T03, NF-90): the house is <paramref name="PropertyKey"/> (a
/// <see cref="DemoPropertyTemplate.Key"/>) and the token is the fixed test token <paramref name="Token"/>, in the alphabet of a base64url token.
/// </summary>
public sealed record DemoWelcomeLinkTemplate(string Key, string PropertyKey, string Token, string Scenario);
