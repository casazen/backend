using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Data.Configurations;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Search;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Property = Casazen.Core.Entities.Property;
using AppContextEntity = Casazen.Core.Entities.AppContext;

namespace Casazen.Infrastructure.Data;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext? tenantContext = null,
    IDataProtectionProvider? dataProtectionProvider = null) : DbContext(options), IDataProtectionKeyContext
{
    // Resolves the caller's OrgId for the global tenant query filter (AC7). Falls back to a
    // no-op (filter disabled) for design-time, background jobs, and unit tests.
    private readonly ITenantContext _tenant = tenantContext ?? NullTenantContext.Instance;

    /// <summary>
    /// Provider of the encrypted columns' converters; part of the model cache key
    /// (<see cref="DataProtectionModelCacheKeyFactory"/>), so a context never encrypts with another context's provider.
    /// </summary>
    internal IDataProtectionProvider? EncryptionProvider { get; } = dataProtectionProvider;

    /// <summary>
    /// ASP.NET Core Data Protection key ring (FD-07, A9-04): persisted here instead of the container
    /// filesystem so encrypted secrets stay readable after a redeploy. Not tenant data.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Org> Orgs { get; set; } = null!;
    public DbSet<OrgSlugAlias> OrgSlugAliases { get; set; } = null!;

    // Operator privacy notice and booking terms of the public site, versioned (BK-14, A3-21)
    public DbSet<OrgSiteDocument> OrgSiteDocuments { get; set; } = null!;

    /// <summary>Custom domains to remove from the Vercel project (BK-17); not tenant-owned, read by the platform job only.</summary>
    public DbSet<PendingDomainRemoval> PendingDomainRemovals { get; set; } = null!;

    /// <summary>The one-time codes of the connection of a site to the org (S022, PLG-BE-CONNECT): hashes only, five minutes each.</summary>
    public DbSet<SiteConnectionCode> SiteConnectionCodes { get; set; } = null!;

    /// <summary>The sites connected to the org, the credentials of their WordPress plugin (S022, PLG-BE-CONNECT): hashes only, revocable.</summary>
    public DbSet<SiteConnection> SiteConnections { get; set; } = null!;
    public DbSet<PropertyActivationCredential> PropertyActivationCredentials => Set<PropertyActivationCredential>();

    public DbSet<PropertyActivationCredentialAssignment> PropertyActivationCredentialAssignments => Set<PropertyActivationCredentialAssignment>();

    public DbSet<OctoratePreparation> OctoratePreparations => Set<OctoratePreparation>();

    public DbSet<PropertyImportReview> PropertyImportReviews => Set<PropertyImportReview>();

    public DbSet<Property> Properties { get; set; } = null!;
    public DbSet<PropertyOwner> PropertyOwners { get; set; } = null!;
    public DbSet<PropertyOwnership> PropertyOwnerships { get; set; } = null!;
    public DbSet<OwnerUser> OwnerUsers { get; set; } = null!;
    public DbSet<OwnerMandate> OwnerMandates { get; set; } = null!;
    public DbSet<OwnerStatement> OwnerStatements { get; set; } = null!;
    public DbSet<StatementLine> StatementLines { get; set; } = null!;
    public DbSet<AgencyInvoice> AgencyInvoices { get; set; } = null!;
    public DbSet<WireBatch> WireBatches { get; set; } = null!;
    public DbSet<WireBatchStatement> WireBatchStatements { get; set; } = null!;
    public DbSet<WireBatchPayout> WireBatchPayouts { get; set; } = null!;
    public DbSet<WithholdingLog> WithholdingLogs { get; set; } = null!;
    public DbSet<F24Record> F24Records { get; set; } = null!;
    public DbSet<OwnerInvitation> OwnerInvitations { get; set; } = null!;
    public DbSet<Booking> Bookings { get; set; } = null!;
    public DbSet<GuestHostExtra> GuestHostExtras => Set<GuestHostExtra>();
    public DbSet<GuestHostExtraOrder> GuestHostExtraOrders => Set<GuestHostExtraOrder>();
    public DbSet<Guest> Guests { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<PaymentRefund> PaymentRefunds { get; set; } = null!;
    public DbSet<PropertyFiscalYear> PropertyFiscalYears { get; set; } = null!;
    public DbSet<OtaIntegration> OtaIntegrations { get; set; } = null!;
    public DbSet<TouristTaxRate> TouristTaxRates { get; set; } = null!;
    public DbSet<OtaSyncLog> OtaSyncLogs { get; set; } = null!;
    public DbSet<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = null!;

    // Stages of the host alerts already sent per stay (CO-10)
    public DbSet<StayAlertState> StayAlertStates { get; set; } = null!;

    // Check-out of a stay: wizard progress and what the host declared (CO-17)
    public DbSet<StayCheckout> StayCheckouts { get; set; } = null!;

    // Stage of the CIN alert already sent per property (CO-20)
    public DbSet<CinAlertState> CinAlertStates { get; set; } = null!;

    // D.L. 145/2023 safety checklist of a short-stay property (CO-07)
    public DbSet<PropertySafetyChecklist> PropertySafetyChecklists { get; set; } = null!;
    public DbSet<PropertySafetyChecklistItem> PropertySafetyChecklistItems { get; set; } = null!;

    // Guests of a stay and official Alloggiati code tables (CO-12)
    public DbSet<StayGuest> StayGuests { get; set; } = null!;
    public DbSet<AlloggiatiCodeEntry> AlloggiatiCodeEntries { get; set; } = null!;
    public DbSet<AlloggiatiCodeTableImport> AlloggiatiCodeTableImports { get; set; } = null!;
    public DbSet<PropertyQuesturaCredentials> PropertyQuesturaCredentials { get; set; } = null!;

    // Who sends the police and ISTAT communications of each property, the attempts of the Alloggiati transmission and the daily receipts
    // of a Questura account (ADR-029, ADR-015, AW-R04, AW-R05; E03/F01/US05/T01)
    public DbSet<PropertyComplianceSender> PropertyComplianceSenders { get; set; } = null!;
    public DbSet<AlloggiatiSendAttempt> AlloggiatiSendAttempts { get; set; } = null!;
    public DbSet<AlloggiatiReceipt> AlloggiatiReceipts { get; set; } = null!;

    // The compliance dashboard "am I compliant?" (E03/F02/US01/T01, NF-52, NF-47): the stage of the period and connection alerts, the monthly
    // summary already sent to an org, the log of what was sent and by whom, and the proofs a host uploads (ids, codes and keys: no guest data)
    public DbSet<ComplianceAlertState> ComplianceAlertStates { get; set; } = null!;
    public DbSet<ComplianceMonthlySummaryLog> ComplianceMonthlySummaryLogs { get; set; } = null!;
    public DbSet<ComplianceSendLogEntry> ComplianceSendLogEntries { get; set; } = null!;
    public DbSet<ManualComplianceProof> ManualComplianceProofs { get; set; } = null!;

    // The flats a receipt covers: one row for each flat of the org an Alloggiati receipt is about (E20/F02/US01/T01, decision O3)
    public DbSet<ManualComplianceProofProperty> ManualComplianceProofProperties { get; set; } = null!;

    // The tracking of the police notification of the Swiss properties (E30/F02/US02/T01, NF-128): the declaration «I uploaded the file» of an arrival day and the audit of the files
    // generated for it (no guest data)
    public DbSet<SwissNotificationDeclaration> SwissNotificationDeclarations { get; set; } = null!;
    public DbSet<SwissNotificationFileEvent> SwissNotificationFileEvents { get; set; } = null!;

    // Official ISTAT list of the comuni and the log of its imports (SU-04)
    public DbSet<Comune> Comuni { get; set; } = null!;
    public DbSet<ComuneImport> ComuneImports { get; set; } = null!;
    public DbSet<OfficialSourceFetch> OfficialSourceFetches { get; set; } = null!;
    public DbSet<ComuneOfficialProfile> ComuneOfficialProfiles { get; set; } = null!;
    public DbSet<ComuneOfficialProfileVersion> ComuneOfficialProfileVersions { get; set; } = null!;

    // Swiss municipalities and localities of the official directory of swisstopo (NF-02, E29/F01/US01/T01, NF-124)
    public DbSet<SwissMunicipality> SwissMunicipalities { get; set; } = null!;
    public DbSet<SwissLocality> SwissLocalities { get; set; } = null!;

    // Swiss compliance receipts (Ticino police notifications, OTR tax reports, ADR-016)
    public DbSet<SwissComplianceReceipt> SwissComplianceReceipts { get; set; } = null!;

    public DbSet<CancellationPolicy> CancellationPolicies { get; set; } = null!;
    public DbSet<PricingAdapterConfig> PricingAdapterConfigs { get; set; } = null!;
    public DbSet<PricingHistory> PricingHistories { get; set; } = null!;
    public DbSet<SeasonalPriceSuggestion> SeasonalPriceSuggestions { get; set; } = null!;
    public DbSet<PropertyDocument> PropertyDocuments { get; set; } = null!;
    public DbSet<PropertyFloorPlan> PropertyFloorPlans { get; set; } = null!;
    public DbSet<PropertyPoi> PropertyPois { get; set; } = null!;

    // The content of a property on the channels of the channel manager: the state on each channel and the sending of its photos (NF-27, NF-32, NF-38)
    public DbSet<ListingChannelState> ListingChannelStates { get; set; } = null!;
    public DbSet<PhotoUploadTask> PhotoUploadTasks { get; set; } = null!;

    // The listing data of a unit the channels ask for: its rooms with their beds and its length-of-stay discounts (NF-34, NF-35)
    public DbSet<PropertyRoom> PropertyRooms { get; set; } = null!;
    public DbSet<PropertyStayDiscount> PropertyStayDiscounts { get; set; } = null!;
    public DbSet<TestDataMarker> TestDataMarkers { get; set; } = null!;
    public DbSet<DemoEnvironmentSetting> DemoEnvironmentSettings { get; set; } = null!;
    public DbSet<SeoContentPage> SeoContentPages { get; set; } = null!;
    public DbSet<SeoContentRevision> SeoContentRevisions { get; set; } = null!;
    public DbSet<SeoContentReviewEvent> SeoContentReviewEvents { get; set; } = null!;
    public DbSet<PlatformAiBudget> PlatformAiBudgets { get; set; } = null!;
    public DbSet<PresetAgentTraining> PresetAgentTraining { get; set; } = null!;
    public DbSet<PlatformAiSettings> PlatformAiSettings { get; set; } = null!;
    public DbSet<PlatformInvoice> PlatformInvoices { get; set; } = null!;
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents { get; set; } = null!;

    // Supplier console (US-022 / #292)
    public DbSet<SupplierProfile> SupplierProfiles { get; set; } = null!;
    public DbSet<SupplierAvailability> SupplierAvailability { get; set; } = null!;

    /// <summary>
    /// Price catalog of the suppliers (SP-02). Keyed by the supplier org, <b>not</b> tenant-filtered (see the TN-2
    /// allow-list): only <c>SupplierServiceCatalogService</c> reads and writes it, always with an explicit
    /// <c>OrgId</c> predicate.
    /// </summary>
    public DbSet<SupplierServiceListing> SupplierServiceListings { get; set; } = null!;

    /// <summary>
    /// Trusted supplier relationships for agency tier orgs (NF-21, ADR-020): an agency's preferred suppliers
    /// with manual payment mode, 0% commission, and statement integration.
    /// </summary>
    public DbSet<TrustedSupplier> TrustedSuppliers { get; set; } = null!;
    public DbSet<GuestSupplierOffer> GuestSupplierOffers => Set<GuestSupplierOffer>();
    public DbSet<GuestSupplierOrderDocument> GuestSupplierOrderDocuments => Set<GuestSupplierOrderDocument>();
    public DbSet<GuestSupplierOrder> GuestSupplierOrders => Set<GuestSupplierOrder>();

    /// <summary>
    /// What an organization remembers of the VAT of a cleaning company («Ricorda per questa ditta», FIX/BE-CLEANING-VAT): tenant-owned, one row for each
    /// organization and company. Only <c>CleaningVatService</c> writes it; the readers of the cleaning VAT read it with the org named.
    /// </summary>
    public DbSet<SupplierVatDefault> SupplierVatDefaults { get; set; } = null!;

    /// <summary>
    /// Internal activities performed by staff on properties (NF-22): cleaning, maintenance, check-in assistance.
    /// Staff sees only activities within their perimeter (PropertyMemberAccess).
    /// </summary>
    public DbSet<InternalActivity> InternalActivities { get; set; } = null!;

    /// <summary>
    /// The default cleaner of a property at the check-out of its stays, with the price and the time agreed (E24/F01/US01/T01, NF-120), and the automatic cleaning
    /// of a stay, the key of the idempotency of the automation (E24/F01/US01/T02). Both are tenant-filtered.
    /// </summary>
    public DbSet<PropertyCleaningSetup> PropertyCleaningSetups { get; set; } = null!;
    public DbSet<StayCleaning> StayCleanings { get; set; } = null!;

    /// <summary>
    /// The supplier's agenda (SP-03): weekly working hours, time off, blocks and extra openings, and the settings row. All
    /// keyed by the supplier org and <b>not</b> tenant-filtered (see the TN-2 allow-list): only <c>SupplierAgendaService</c>
    /// (and the supplier repair) reads and writes them, always with an explicit <c>OrgId</c> predicate.
    /// </summary>
    public DbSet<SupplierWorkingHours> SupplierWorkingHours { get; set; } = null!;
    public DbSet<SupplierTimeOff> SupplierTimeOff { get; set; } = null!;
    public DbSet<SupplierBusyWindow> SupplierBusyWindows { get; set; } = null!;
    public DbSet<SupplierFreeCalendarEvent> SupplierFreeCalendarEvents { get; set; } = null!;
    public DbSet<SupplierSettings> SupplierSettings { get; set; } = null!;

    /// <summary>
    /// What weighs on the reliability of a supplier (SP-REL): replies in time, missed deadlines, completed jobs, disputes. Keyed by the
    /// supplier org and <b>not</b> tenant-filtered (TN-2 allow-list): only <c>SupplierReliabilityService</c> reads and writes it, always
    /// with an explicit supplier predicate.
    /// </summary>
    public DbSet<SupplierReliabilityEvent> SupplierReliabilityEvents { get; set; } = null!;

    /// <summary>
    /// The content of the suppliers' public showcases (SP-13a): the draft, the published version and the one before it, one row per
    /// supplier. Keyed by the supplier org, <b>not</b> tenant-filtered (see the TN-2 allow-list): only
    /// <c>SupplierShowcaseContentService</c> (and the supplier repair) reads and writes it, always with an explicit <c>OrgId</c> predicate.
    /// </summary>
    public DbSet<SupplierShowcaseContent> SupplierShowcaseContents { get; set; } = null!;

    /// <summary>
    /// The inventory of the images a supplier uploaded for its showcase (SP-13b). Keyed by the supplier org like the content and not tenant-filtered
    /// for the same reason; only <c>SupplierShowcaseMediaService</c> (and the content service, which reads the keys of the supplier's own rows)
    /// uses it, always with an explicit <c>OrgId</c> predicate, or as a system job over the rows that have to go (the cleanup).
    /// </summary>
    public DbSet<SupplierShowcaseMedia> SupplierShowcaseMedia { get; set; } = null!;
    public DbSet<SupplierInviteRecord> SupplierInviteRecords { get; set; } = null!;
    public DbSet<SupplierAdminAuditEntry> SupplierAdminAuditEntries { get; set; } = null!;
    public DbSet<HostInviteRecord> HostInviteRecords { get; set; } = null!;
    public DbSet<HostInviteAuditEntry> HostInviteAuditEntries { get; set; } = null!;
    public DbSet<ServiceRequest> ServiceRequests { get; set; } = null!;

    /// <summary>
    /// The booking from a supplier's public showcase (SP-10): the private customers and the holds that wait for the e-mail check.
    /// Both keyed by the supplier org and <b>not</b> tenant-filtered (see the TN-2 allow-list); only the booking service and the
    /// few readers listed in <c>ShowcaseBookingTenancyTests</c> touch them, always with an explicit <c>OrgId</c> predicate.
    /// </summary>
    public DbSet<ServiceCustomer> ServiceCustomers { get; set; } = null!;
    public DbSet<ShowcaseBookingHold> ShowcaseBookingHolds { get; set; } = null!;

    /// <summary>
    /// The request on measure from a supplier's public showcase (SP-10b): the inquiry that waits for the e-mail check and the messages of
    /// the thread of the request. Both keyed by the supplier org and <b>not</b> tenant-filtered (see the TN-2 allow-list); only the services
    /// listed in <c>ShowcaseInquiryTenancyTests</c> touch them, always with an explicit <c>OrgId</c> predicate.
    /// </summary>
    public DbSet<ShowcaseInquiry> ShowcaseInquiries { get; set; } = null!;
    public DbSet<ServiceRequestMessage> ServiceRequestMessages { get; set; } = null!;

    // Payment of the service requests inside CasaZen (SP-15a): two parties, not tenant-filtered.
    public DbSet<ServiceRequestPayment> ServiceRequestPayments { get; set; } = null!;

    // The refunds of a service payment (SP-15b): they belong to the payment, so they are not tenant-filtered either.
    public DbSet<ServiceRequestPaymentRefund> ServiceRequestPaymentRefunds { get; set; } = null!;

    // The complaint about a job paid through CasaZen and its dispute (SP-15c-c): it belongs to the payment, so it is not tenant-filtered either.
    public DbSet<ServicePaymentDispute> ServicePaymentDisputes { get; set; } = null!;

    /// <summary>
    /// The groups of backup suppliers (SP-BKP): a request and the backups it was sent to with it. Two parties and more (the host and
    /// the suppliers of the backups) and a system job, so <b>not</b> tenant-filtered (see the TN-2 allow-list); only the code listed in
    /// <c>ServiceRequestGroupTenancyTests</c> touches it, always with an explicit predicate.
    /// </summary>
    public DbSet<ServiceRequestGroup> ServiceRequestGroups { get; set; } = null!;

    // Property iCal OTA sync (US-018 / #294)
    public DbSet<CalendarBlock> CalendarBlocks { get; set; } = null!;
    public DbSet<PropertyICalFeed> PropertyICalFeeds { get; set; } = null!;
    public DbSet<PropertyICalExport> PropertyICalExports { get; set; } = null!;

    // Scheduled changes of the rental mode of a property, and their history (PM-02)
    public DbSet<PropertyModeChange> PropertyModeChanges { get; set; } = null!;

    // Guest self-service check-in portal (US-020 / #296)
    public DbSet<GuestCheckInSession> GuestCheckInSessions { get; set; } = null!;

    // Where the check-in link of a booking stands on its way to the guest: the portal chat, the e-mail, the host (E23/F01/US01/T01, T02, NF-122)
    public DbSet<CheckInLinkDelivery> CheckInLinkDeliveries { get; set; } = null!;

    /// <summary>The messages of the routines planned for each booking (E05/F07/US01/T01, NF-71).</summary>
    public DbSet<ScheduledMessage> ScheduledMessages { get; set; } = null!;

    // Guest identity document images for OCR (NF-10, ADR-013-R3)
    public DbSet<DocumentImage> DocumentImages { get; set; } = null!;

    // Biometric verification settings and audit logs (NF-11, ADR-013)
    public DbSet<BiometricSettings> BiometricSettings { get; set; } = null!;

    // Which steps of a stay CasaZen does by itself and which the host does (casazen/backend#978): one row per org.
    public DbSet<BookingFlowSettings> BookingFlowSettings { get; set; } = null!;
    public DbSet<BiometricVerificationLog> BiometricVerificationLogs { get; set; } = null!;

    // The self-hosted identity verification (E70/F01/US01/T02, NF-194, NF-195): the segments (property × format × channel) and the evidence kept for the reviewer.
    public DbSet<IdentitySegmentSetting> IdentitySegmentSettings { get; set; } = null!;
    public DbSet<IdentityReviewEvidence> IdentityReviewEvidences { get; set; } = null!;

    // Property access codes and release logs (NF-13, E06/F03/US01/T01)
    public DbSet<PropertyAccessCode> PropertyAccessCodes { get; set; } = null!;
    public DbSet<AccessCodeReleaseLog> AccessCodeReleaseLogs { get; set; } = null!;

    // Native host app push tokens (US-025 / #299)
    public DbSet<DeviceRegistration> DeviceRegistrations { get; set; } = null!;

    // Push messages per event and device, with their Expo ticket (MO-04)
    public DbSet<PushDelivery> PushDeliveries { get; set; } = null!;

    // The bell of the shell: one row per user and event, written next to the push of the same event (UI-12a)
    public DbSet<InAppNotification> InAppNotifications { get; set; } = null!;

    // The popup alerts of a user (shown outside the wizard, «Do now» / «Remind me later») and the quiet hours that hold them back (NF-08, NF-22, ADR-025)
    public DbSet<PopupAlert> PopupAlerts { get; set; } = null!;
    public DbSet<UserNotificationPreferences> UserNotificationPreferences { get; set; } = null!;

    // What each user chose for each kind of event, and the slot of the one-email-every-ten-minutes rule (E05/F03, NF-65)
    public DbSet<UserNotificationChannelSetting> UserNotificationChannelSettings { get; set; } = null!;
    public DbSet<NotificationEmailSlot> NotificationEmailSlots { get; set; } = null!;

    // Approval queue for items requiring human decision before taking effect (NF-06)
    public DbSet<ApprovalQueueItem> ApprovalQueueItems { get; set; } = null!;

    public DbSet<TerritorialRentAgreement> TerritorialRentAgreements { get; set; } = null!;
    public DbSet<ConcordatoRentBand> ConcordatoRentBands { get; set; } = null!;
    public DbSet<TerritorialAgreementSignatory> TerritorialAgreementSignatories { get; set; } = null!;
    public DbSet<HighTensionAreaComune> HighTensionAreaComuni { get; set; } = null!;
    public DbSet<ComuneImuChannel> ComuneImuChannels { get; set; } = null!;
    public DbSet<RegulatoryDataAuditEntry> RegulatoryDataAuditEntries { get; set; } = null!;

    // Long-term lease
    public DbSet<LeaseDocument> LeaseDocuments { get; set; } = null!;
    public DbSet<LeaseContract> LeaseContracts { get; set; } = null!;
    public DbSet<Party> Parties { get; set; } = null!;
    public DbSet<LeaseRegistration> LeaseRegistrations { get; set; } = null!;
    public DbSet<LeaseEvent> LeaseEvents { get; set; } = null!;
    public DbSet<LeaseMaintenanceRequest> LeaseMaintenanceRequests { get; set; } = null!;
    public DbSet<LeaseRegistrationAuthorization> LeaseRegistrationAuthorizations { get; set; } = null!;

    public DbSet<OpenapiLeaseRegistrationPayment> OpenapiLeaseRegistrationPayments { get; set; } = null!;
    public DbSet<LeaseSigner> LeaseSigners { get; set; } = null!;
    public DbSet<RentSchedule> RentSchedules { get; set; } = null!;
    public DbSet<RentLedgerEntry> RentLedgerEntries { get; set; } = null!;
    public DbSet<RentLedgerComponent> RentLedgerComponents { get; set; } = null!;
    public DbSet<TenantPaymentDeclaration> TenantPaymentDeclarations { get; set; } = null!;
    public DbSet<LeaseCondominiumTerms> LeaseCondominiumTerms { get; set; } = null!;
    public DbSet<TenantCondominiumSettlement> TenantCondominiumSettlements { get; set; } = null!;
    public DbSet<TenantExpenseAllocation> TenantExpenseAllocations { get; set; } = null!;

    public DbSet<Condominium> Condominiums { get; set; } = null!;
    public DbSet<CondominiumUnit> CondominiumUnits { get; set; } = null!;
    public DbSet<CondominiumPeriod> CondominiumPeriods { get; set; } = null!;
    public DbSet<CondominiumStatement> CondominiumStatements { get; set; } = null!;
    public DbSet<CondominiumExpenseLine> CondominiumExpenseLines { get; set; } = null!;
    public DbSet<CondominiumInstallment> CondominiumInstallments { get; set; } = null!;
    public DbSet<CondominiumPriorBalance> CondominiumPriorBalances { get; set; } = null!;
    public DbSet<CondominiumPayment> CondominiumPayments { get; set; } = null!;
    public DbSet<CondominiumImportBatch> CondominiumImportBatches { get; set; } = null!;
    public DbSet<CondominiumImportHouse> CondominiumImportHouses { get; set; } = null!;
    public DbSet<CondominiumImportYear> CondominiumImportYears { get; set; } = null!;
    public DbSet<CondominiumStatementProposal> CondominiumStatementProposals { get; set; } = null!;
    public DbSet<RentBankMovement> RentBankMovements { get; set; } = null!;
    public DbSet<BankLinkPayment> BankLinkPayments { get; set; } = null!;

    // ConcordatoFacile integration (NF-02, NF-03, ADR-004)
    public DbSet<ConcordatoFacilePurchase> ConcordatoFacilePurchases { get; set; } = null!;
    public DbSet<ConcordatoFacileGift> ConcordatoFacileGifts { get; set; } = null!;

    // Practices of the simulated ConcordatoFacile module: test environments only (CF-SIM)
    public DbSet<CfSimulatedPractice> CfSimulatedPractices { get; set; } = null!;

    public DbSet<AppContextEntity> AppContexts { get; set; } = null!;
    public DbSet<ConsentRecord> ConsentRecords { get; set; } = null!;
    public DbSet<GuestConsentRecord> GuestConsentRecords { get; set; } = null!;
    public DbSet<MarketingConsentRecord> MarketingConsentRecords { get; set; } = null!;
    public DbSet<SuppressionListEntry> SuppressionListEntries { get; set; } = null!;

    // CRM guest profiles for marketing and segmentation (NF-23, ADR-021)
    public DbSet<GuestCrmProfile> GuestCrmProfiles { get; set; } = null!;

    // CRM contacts with origin, double opt-in, legal basis and suppression, the proposed merges of duplicate profiles and the welcome QR link of a property (NF-88, NF-89, NF-90)
    public DbSet<GuestContact> GuestContacts { get; set; } = null!;
    public DbSet<GuestMergeCandidate> GuestMergeCandidates { get; set; } = null!;
    public DbSet<PropertyWelcomeLink> PropertyWelcomeLinks { get; set; } = null!;

    // The personal stay link of a booking, the address of the guest portal (E50/F01/US01/T01, NF-152)
    public DbSet<GuestStayChange> GuestStayChanges { get; set; } = null!;

    public DbSet<GuestPortalLink> GuestPortalLinks { get; set; } = null!;

    // Marketing campaigns and segments (NF-24, ADR-014)
    public DbSet<GuestSegment> GuestSegments { get; set; } = null!;
    public DbSet<MarketingStudioSettings> MarketingStudioSettings => Set<MarketingStudioSettings>();
    public DbSet<MarketingAiUsage> MarketingAiUsage => Set<MarketingAiUsage>();
    public DbSet<MarketingAsset> MarketingAssets => Set<MarketingAsset>();
    public DbSet<MarketingEventSearch> MarketingEventSearches => Set<MarketingEventSearch>();
    public DbSet<CampaignDelivery> CampaignDeliveries => Set<CampaignDelivery>();
    public DbSet<Campaign> Campaigns { get; set; } = null!;

    // Automation routines and execution log (NF-26, ADR-022)
    public DbSet<Routine> Routines { get; set; } = null!;
    public DbSet<RoutineExecution> RoutineExecutions { get; set; } = null!;

    // Transactional outbox of the domain events, published on the event bus by the dispatcher (ADR-022-R1, R2)
    public DbSet<OutboxEvent> OutboxEvents { get; set; } = null!;

    // The progress of the wizards, saved on the server so that a person resumes it on any device (ADR-024)
    public DbSet<WizardDraft> WizardDrafts { get; set; } = null!;

    // The connection of an org to its Octorate account, one per environment, with its OAuth tokens encrypted (ADR-023, E02/F01/US01/T01)
    public DbSet<OctorateConnection> OctorateConnections { get; set; } = null!;

    // The listings found on the channel manager and their mapping to the CasaZen properties, and the single-use OAuth states of the
    // consent (ADR-023, ADR-027, E02/F01/US02/T02)
    public DbSet<OctorateListingMapping> OctorateListingMappings { get; set; } = null!;
    public DbSet<OctorateOAuthState> OctorateOAuthStates { get; set; } = null!;

    // The import of the listings that already exist on OTAs or in another channel manager (D6, E02/F01/US03): what a person declared about where the
    // listings of a property are, the import batches, their channels and the external items done for each
    public DbSet<OctorateInventoryIntent> OctorateInventoryIntents { get; set; } = null!;
    public DbSet<OctorateInventoryImportBatch> OctorateInventoryImportBatches { get; set; } = null!;
    public DbSet<OctorateInventoryImportChannel> OctorateInventoryImportChannels { get; set; } = null!;
    public DbSet<OctorateInventoryImportItem> OctorateInventoryImportItems { get; set; } = null!;

    // The mirror of the CasaZen bookings on Octorate: the state of the last operation sent for each booking (E08/F01/US01/T01, ADR-031)
    public DbSet<OctorateBookingSync> OctorateBookingSyncs { get; set; } = null!;

    // Who an invoice of a booking is made out to, one row for each booking, personal data encrypted (FIS-01, NF-81, E06/F02/US01/T01)
    public DbSet<BookingInvoiceData> BookingInvoiceData { get; set; } = null!;

    // Who issues the documents of an organization (one profile for each org) and the single issuer of each booking (FIS-02, NF-82, E06/F02/US02/T01)
    public DbSet<OrgFiscalIssuerProfile> OrgFiscalIssuerProfiles { get; set; } = null!;
    public DbSet<BookingDocumentIssuer> BookingDocumentIssuers { get; set; } = null!;

    // The fiscal documents issued by CasaZen or imported from Octorate, their lines and the counters of their numbers (NF-83, NF-84, E06/F01/US02/T01)
    public DbSet<FiscalDocument> FiscalDocuments { get; set; } = null!;
    public DbSet<FiscalDocumentLine> FiscalDocumentLines { get; set; } = null!;
    public DbSet<FiscalNumberSequence> FiscalNumberSequences { get; set; } = null!;

    // How the package of the accountant is sent to the accountant, one row for each org, the address encrypted (NF-101, E15/F01/US02/T01)
    public DbSet<AccountantPackageSettings> AccountantPackageSettings { get; set; } = null!;

    // The owner accounting of the agencies: the approved rule sets and the effective-dated fiscal profiles of the owners (NF-109, NF-110, E06/F05/US02/T01)
    public DbSet<OwnerAccountingPolicy> OwnerAccountingPolicies { get; set; } = null!;
    public DbSet<OwnerFiscalProfile> OwnerFiscalProfiles { get; set; } = null!;

    // The owner ledger and the month-end close (NF-110, E06/F05/US02/T02): the append-only lines, the statement versions, the months, the charges and the exchange rates
    public DbSet<OwnerLedgerEntry> OwnerLedgerEntries { get; set; } = null!;
    public DbSet<OwnerStatementVersion> OwnerStatementVersions { get; set; } = null!;
    public DbSet<OwnerClosePeriod> OwnerClosePeriods { get; set; } = null!;
    public DbSet<OwnerCharge> OwnerCharges { get; set; } = null!;
    public DbSet<OwnerFxRate> OwnerFxRates { get; set; } = null!;

    // The commission rules of the channels of an agency, when a reservation brings none (E13/F01/US01/T01, ⚖ R2)
    public DbSet<OrgChannelCommissionRule> OrgChannelCommissionRules { get; set; } = null!;

    // Who holds the account of a listing on a portal when it is not the organization default (E14/F01/US01/T01, ⚖ I10)
    public DbSet<PropertyChannelAccountHolder> PropertyChannelAccountHolders { get; set; } = null!;

    // The links from the owner statement versions to the invoices of the agency's external fiscal system (E06/F01/US02/T05): ids, numbers, hashes
    public DbSet<OwnerExternalDocumentReference> OwnerExternalDocumentReferences { get; set; } = null!;

    // The payouts to the owners, their append-only withholding evidence and the annual tax datasets (E06/F05/US02/T03, NF-110)
    public DbSet<OwnerPayout> OwnerPayouts { get; set; } = null!;

    // The owner portal operations (E07/F07/US01, NF-111): the rules and the requests of the owner stays; the shared documents, their versions and
    // downloads, the delegated contacts and the inbox.
    public DbSet<OwnerStayRule> OwnerStayRules { get; set; } = null!;
    public DbSet<OwnerStayRequest> OwnerStayRequests { get; set; } = null!;
    public DbSet<OwnerDocument> OwnerDocuments { get; set; } = null!;
    public DbSet<OwnerDocumentVersion> OwnerDocumentVersions { get; set; } = null!;
    public DbSet<OwnerDocumentDownload> OwnerDocumentDownloads { get; set; } = null!;
    public DbSet<OwnerPortalContact> OwnerPortalContacts { get; set; } = null!;
    public DbSet<OwnerConversation> OwnerConversations { get; set; } = null!;
    public DbSet<OwnerMessage> OwnerMessages { get; set; } = null!;

    public DbSet<TaxObligationEvidence> TaxObligationEvidence { get; set; } = null!;
    public DbSet<TaxReportingDataset> TaxReportingDatasets { get; set; } = null!;

    // What a person decided about the tax regime of the stay of a company tenant in the property of a private owner: append only (E18/F01/US03/T01, ⚖ I7)
    public DbSet<ShortLetTaxDecision> ShortLetTaxDecisions { get; set; } = null!;

    // The check-in guests and the states of a stay sent to Octorate (NF-41, NF-45, E03/F01/US01/T03): ids, hashes and codes, no personal data
    public DbSet<StayGuestOctorateLink> StayGuestOctorateLinks { get; set; } = null!;
    public DbSet<OctorateStayStateSync> OctorateStayStateSyncs { get; set; } = null!;

    public DbSet<GuestPrivacyAuditEntry> GuestPrivacyAuditEntries { get; set; } = null!;
    public DbSet<SignupAttribution> SignupAttributions { get; set; } = null!;
    public DbSet<SeoEvent> SeoEvents { get; set; } = null!;
    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<RolePermission> RolePermissions { get; set; } = null!;
    public DbSet<UserContextMembership> UserContextMemberships { get; set; } = null!;
    public DbSet<OrgMember> OrgMembers { get; set; } = null!;
    public DbSet<OrgInvitation> OrgInvitations { get; set; } = null!;

    // The properties a «Solo alcuni» collaborator will reach once it accepts its invitation (AM-02c)
    public DbSet<OrgInvitationProperty> OrgInvitationProperties { get; set; } = null!;
    public DbSet<PropertyMemberAccess> PropertyMemberAccesses { get; set; } = null!;

    // Who did what in an org, as ids and codes (AM-02b)
    public DbSet<OrgActivityEntry> OrgActivityEntries { get; set; } = null!;

    // The verified reviews of the stays, the reports of the hosts about them and the mark that a guest was asked to write one (DB-12a)
    public DbSet<Review> Reviews { get; set; } = null!;
    public DbSet<ReviewReport> ReviewReports { get; set; } = null!;
    public DbSet<ReviewInvite> ReviewInvites { get; set; } = null!;

    // The messages between the guest of a direct booking and the host: one thread per booking (DB-14a)
    public DbSet<StorefrontChatThread> StorefrontChatThreads { get; set; } = null!;
    public DbSet<StorefrontChatMessage> StorefrontChatMessages { get; set; } = null!;

    public DbSet<GuestThread> GuestThreads { get; set; } = null!;
    public DbSet<GuestMessage> GuestMessages { get; set; } = null!;

    // The copy of the conversations of the guests with the channels (Airbnb, Booking.com, Vrbo, Expedia), read from the channel manager: the threads,
    // their messages (the words encrypted), the checkpoints of the sync of each listing, and the message templates of the hosts (E05/F01/US01/T01, ADR-032)
    public DbSet<OtaMessageThread> OtaMessageThreads { get; set; } = null!;
    public DbSet<OtaMessage> OtaMessages { get; set; } = null!;
    public DbSet<OtaMessageSyncState> OtaMessageSyncStates { get; set; } = null!;
    public DbSet<MessageTemplate> MessageTemplates { get; set; } = null!;

    // What the channel manager says about the reviews of a booking of a channel (Airbnb): the guest's review, the host's reply and the host's own review, with the state
    // of each and of the read; the words are encrypted (E05/F06/US01/T01, ADR-032, NF-69). The reviews of the booking site are the Reviews above.
    public DbSet<OtaReview> OtaReviews { get; set; } = null!;

    // The assistant that answers the guests' messages (E05/F05/US01/T01, NF-66, NF-67): the knowledge base of a property and of an organization (the free text
    // encrypted), the rules of the organization and the exceptions of a property, what it does in each situation, the lessons it proposes, the log of what it
    // decided for each message (ids and codes only) and the changes of trust level it proposes
    public DbSet<PropertyKnowledgeEntry> PropertyKnowledgeEntries { get; set; } = null!;
    public DbSet<OrgAssistantRules> OrgAssistantRules { get; set; } = null!;
    public DbSet<PropertyAssistantSettings> PropertyAssistantSettings { get; set; } = null!;
    public DbSet<AssistantSituationSetting> AssistantSituationSettings { get; set; } = null!;
    public DbSet<AssistantLesson> AssistantLessons { get; set; } = null!;
    public DbSet<AssistantDecisionLog> AssistantDecisionLogs { get; set; } = null!;
    public DbSet<AssistantTrustProposal> AssistantTrustProposals { get; set; } = null!;

    // The tourist tax a host paid to a comune and the receipt of the payment: the «paid» half of the summary per comune (NF-100, REP-05)
    public DbSet<TouristTaxRemittance> TouristTaxRemittances { get; set; } = null!;

    // The tourist tax per guest (NF-46, ADR-037, E03/F01/US07/T01): the exemption reasons and the channel rules (platform reference data),
    // the rate a host declares for a comune with no official one and the confirmed tax of a stay (tenant-owned; its lines are reached only
    // through it)
    public DbSet<TouristTaxExemptionReason> TouristTaxExemptionReasons { get; set; } = null!;
    public DbSet<TouristTaxChannelRule> TouristTaxChannelRules { get; set; } = null!;
    public DbSet<OrgTouristTaxRate> OrgTouristTaxRates { get; set; } = null!;
    public DbSet<StayTouristTax> StayTouristTaxes { get; set; } = null!;

    // The ISTAT region table (E21/F01/US01/T01, decision O6): for each region and autonomous province the system of its portal, the deadline, the statistical
    // fields it requires and whether the channel manager covers it. Platform reference data, edited only by the staff, every seeded row «da verificare»
    public DbSet<IstatRegionRule> IstatRegionRules { get; set; } = null!;

    // The marketplace (E09/F01, NF-54): the portal catalog cached from Octorate, the partners the platform admin curates with their texts
    // (platform data, no tenant), and what each org has activated (tenant-owned)
    public DbSet<MarketplacePortal> MarketplacePortals { get; set; } = null!;
    public DbSet<MarketplacePartner> MarketplacePartners { get; set; } = null!;
    public DbSet<MarketplacePartnerText> MarketplacePartnerTexts { get; set; } = null!;
    public DbSet<MarketplaceActivation> MarketplaceActivations { get; set; } = null!;

    // The activation of CasaZen with Octorate: what the host chose to do later for each part of the plan, the flows activated on each property, the
    // guided steps of the Octorate panel, the «Do it for me» requests, the pilot group of an agency and the per-property data (E10/F01/US01/T01, NF-104..NF-107)
    public DbSet<OnboardingPartState> OnboardingPartStates { get; set; } = null!;
    public DbSet<PropertyFlowActivation> PropertyFlowActivations { get; set; } = null!;
    public DbSet<OnboardingPanelStep> OnboardingPanelSteps { get; set; } = null!;
    public DbSet<OnboardingAssistRequest> OnboardingAssistRequests { get; set; } = null!;
    public DbSet<OnboardingPilotMember> OnboardingPilotMembers { get; set; } = null!;
    public DbSet<PropertyOnboardingData> PropertyOnboardingData { get; set; } = null!;

    // The support vault of «Do it for me» (E20/F03/US01/T01, decision O5): the credentials support keeps for a host, every opening of them, and the
    // regional ISTAT credentials the host enters (write-only); the values are encrypted at rest (EncryptedColumns)
    public DbSet<SupportVaultSecret> SupportVaultSecrets { get; set; } = null!;
    public DbSet<SupportVaultAccess> SupportVaultAccesses { get; set; } = null!;
    public DbSet<PropertyIstatCredentials> PropertyIstatCredentials { get; set; } = null!;

    // The price and the rules of each night of a property, and the changes of the calendar the host sent to the channel manager with their chunks
    // (E08/F05/US01/T01, NF-36): dates, numbers, states and stable codes, no personal data
    public DbSet<PropertyDayRule> PropertyDayRules { get; set; } = null!;
    public DbSet<CalendarChangeProcess> CalendarChangeProcesses { get; set; } = null!;
    public DbSet<CalendarChangeChunk> CalendarChangeChunks { get; set; } = null!;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // FD-06: every DateTime is UTC in and out of PostgreSQL timestamptz columns.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeValueConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeValueConverter>();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, DataProtectionModelCacheKeyFactory>();

    /// <summary>Refuses a change of the evidence before anything is written (<see cref="AppendOnlyGuard"/>, E06/F05/US02/T02).</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AppendOnlyGuard.Check(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc cref="SaveChanges(bool)"/>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AppendOnlyGuard.Check(ChangeTracker);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new GuestSupplierOfferConfiguration());
        modelBuilder.ApplyConfiguration(new GuestSupplierOrderConfiguration());
        modelBuilder.ApplyConfiguration(new GuestSupplierOrderDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyActivationCredentialConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyActivationCredentialAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new OctoratePreparationConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyImportReviewConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyConfiguration());
        modelBuilder.ApplyConfiguration(new BookingConfiguration());
        modelBuilder.ApplyConfiguration(new GuestConfiguration());
        modelBuilder.ApplyConfiguration(new AlloggiatiWebReportConfiguration());
        modelBuilder.ApplyConfiguration(new StayAlertStateConfiguration());

        // CO-17: the check-out of a stay goes with it; its cleaning request survives it as a plain request.
        modelBuilder.ApplyConfiguration(new StayCheckoutConfiguration());
        modelBuilder.ApplyConfiguration(new CinAlertStateConfiguration());

        // CO-12: the guests of a stay follow their booking; the booker link survives the booker's deletion as null.
        modelBuilder.ApplyConfiguration(new StayGuestConfiguration());

        // CO-15: privacy history of a guest (notice presented, marketing consent), removed with the guest record.
        modelBuilder.ApplyConfiguration(new GuestConsentRecordConfiguration());

        // CO-15: audit of the operations on guest data; no foreign key to the guest, so it outlives a removed guest.
        modelBuilder.ApplyConfiguration(new GuestPrivacyAuditEntryConfiguration());

        // NF-05: marketing suppression list with SHA-256 blind index for one-click unsubscribe (ADR-007-R18, R22).
        modelBuilder.ApplyConfiguration(new SuppressionListEntryConfiguration());

        // NF-88, NF-89, NF-90: the CRM contacts (value encrypted at rest, found by its keyed hash), the proposed merges of two profiles (one row for
        // each pair) and the welcome QR link of a property (one active link for each property); each carries its org (TN-2).
        modelBuilder.ApplyConfiguration(new GuestContactConfiguration());
        modelBuilder.ApplyConfiguration(new GuestMergeCandidateConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyWelcomeLinkConfiguration());

        // E50/F01/US01/T01 (NF-152): the stay link of a booking (the guest portal), one active link for each booking; it carries the org of the booking (TN-2).
        modelBuilder.ApplyConfiguration(new GuestPortalLinkConfiguration());
        modelBuilder.ApplyConfiguration(new GuestStayChangeConfiguration());

        // CO-07: one safety checklist per property, going with it; its items go with the checklist. An evidence
        // document deleted by the host leaves the item without proof (SET NULL), never deletes the answer.
        modelBuilder.ApplyConfiguration(new PropertySafetyChecklistConfiguration());

        modelBuilder.ApplyConfiguration(new PropertySafetyChecklistItemConfiguration());

        // DB-11a: the floor plan levels and the points of interest of a property, going with it; each carries the org of its property (TN-2).
        modelBuilder.ApplyConfiguration(new PropertyFloorPlanConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyPoiConfiguration());

        // NF-27, NF-32, NF-38: the state of a property on each channel of the channel manager (unique per property and channel) and the sending
        // of its photos (unique per property and photo address); each carries the org of its property (TN-2) and goes with it.
        modelBuilder.ApplyConfiguration(new ListingChannelStateConfiguration());
        modelBuilder.ApplyConfiguration(new PhotoUploadTaskConfiguration());

        // NF-34, NF-35: the rooms of a unit with their beds and its length-of-stay discounts (one tier for each minimum of nights); each carries
        // the org of its property (TN-2) and goes with it.
        modelBuilder.ApplyConfiguration(new PropertyRoomConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyStayDiscountConfiguration());
        modelBuilder.ApplyConfiguration(new AlloggiatiCodeEntryConfiguration());

        // SU-04: official ISTAT list of the comuni. The ISTAT code is the key; the cadastral code is unique among the active
        // comuni only (a comune that changes province gets a new ISTAT code and keeps its cadastral code, so the old row,
        // deactivated, and the new one coexist).
        modelBuilder.ApplyConfiguration(new ComuneConfiguration());
        modelBuilder.ApplyConfiguration(new ComuneImportConfiguration());
        modelBuilder.ApplyConfiguration(new OfficialSourceFetchConfiguration());
        modelBuilder.ApplyConfiguration(new ComuneOfficialProfileConfiguration());
        modelBuilder.ApplyConfiguration(new ComuneOfficialProfileVersionConfiguration(Database.IsNpgsql()));

        modelBuilder.ApplyConfiguration(new SwissLocalityConfiguration());

        // CO-14: one set of Alloggiati Web credentials per property, going with it; tenant row (TN-2).
        modelBuilder.ApplyConfiguration(new PropertyQuesturaCredentialsConfiguration());

        // ADR-029, ADR-015 (E03/F01/US05/T01): the sender of each property (never two), the attempts of the Alloggiati transmission (at
        // most one open per booking) and the daily receipts of a Questura account (one per org, account, day and mode); tenant rows (TN-2).
        modelBuilder.ApplyConfiguration(new PropertyComplianceSenderConfiguration());
        modelBuilder.ApplyConfiguration(new AlloggiatiSendAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new AlloggiatiReceiptConfiguration());

        // NF-52, NF-47 (E03/F02/US01/T01): the alerts of a period or a connection sent once (unique per property, kind and reference), the monthly
        // summary sent once per org and month, the log of the sends and the proofs a host uploads; tenant rows (TN-2).
        modelBuilder.ApplyConfiguration(new ComplianceAlertStateConfiguration());
        modelBuilder.ApplyConfiguration(new ComplianceMonthlySummaryLogConfiguration());
        modelBuilder.ApplyConfiguration(new ComplianceSendLogEntryConfiguration());
        modelBuilder.ApplyConfiguration(new ManualComplianceProofConfiguration());

        // E20/F02/US01/T01 (decision O3): the flats a receipt covers, one row for each, with the unique pair (proof, flat); a tenant row (TN-2).
        modelBuilder.ApplyConfiguration(new ManualComplianceProofPropertyConfiguration());

        // E30/F02/US02/T01 (NF-128): the declaration «I uploaded the file» of an arrival day (one for each property, day and regime: two partial unique indexes, a null regime is the
        // notification to the Ticino police) and the audit of the files generated for the police (a format and a number of rows, never a guest); tenant rows (TN-2).
        modelBuilder.ApplyConfiguration(new SwissNotificationDeclarationConfiguration());
        modelBuilder.ApplyConfiguration(new SwissNotificationFileEventConfiguration());
        modelBuilder.Entity<Booking>(BookingConfiguration.ConfigureLeMiePrenotazioni);
        modelBuilder.Entity<Property>(PropertyConfiguration.ConfigureGPSCoordinates);
        modelBuilder.Entity<Booking>(BookingConfiguration.ConfigurePropertyIdIndex);
        modelBuilder.ApplyConfiguration(new PaymentConfiguration());

        // Refunds on Stripe (BK-02): one row per Stripe refund, one Stripe request per row (idempotency key).
        modelBuilder.ApplyConfiguration(new PaymentRefundConfiguration());
        modelBuilder.ApplyConfiguration(new OtaIntegrationConfiguration());

        // Every encrypted column (OTA secrets, iCal import URLs, guest identity documents, Questura credentials) is
        // declared and configured in one place, EncryptedColumns, with the Data Protection value converter
        // (PC-11, CO-14, docs/runbooks/encryption.md).
        if (EncryptionProvider is not null)
            EncryptedColumns.Configure(modelBuilder, EncryptionProvider);

        modelBuilder.ApplyConfiguration(new TouristTaxRateConfiguration());
        modelBuilder.ApplyConfiguration(new SeoContentPageConfiguration());
        modelBuilder.ApplyConfiguration(new SeoContentRevisionConfiguration());
        modelBuilder.Entity<SeoContentPage>(SeoContentPageConfiguration.ConfigurePublishedRevisionIdForeignKey);
        modelBuilder.Entity<SeoContentRevision>(SeoContentRevisionConfiguration.ConfigureContentStatus);
        modelBuilder.ApplyConfiguration(new SeoContentReviewEventConfiguration());
        modelBuilder.Entity<Guest>(GuestConfiguration.ConfigureEmailIndex);
        modelBuilder.ApplyConfiguration(new PricingAdapterConfigConfiguration());
        modelBuilder.ApplyConfiguration(new PricingHistoryConfiguration());

        // PC-15: one seasonal suggestion per property and stay date, regenerated in place (upsert by date).
        modelBuilder.ApplyConfiguration(new SeasonalPriceSuggestionConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyDocumentConfiguration());

        // Demo environment: one platform row, and one marker per invented row so a reset never touches host data.
        modelBuilder.ApplyConfiguration(new DemoEnvironmentSettingConfiguration());
        modelBuilder.ApplyConfiguration(new TestDataMarkerConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseContractConfiguration());
        modelBuilder.ApplyConfiguration(new PartyConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseEventConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseSignerConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseRegistrationAuthorizationConfiguration());
        modelBuilder.ApplyConfiguration(new OpenapiLeaseRegistrationPaymentConfiguration());
        modelBuilder.ApplyConfiguration(new RentScheduleConfiguration());
        modelBuilder.ApplyConfiguration(new RentLedgerEntryConfiguration());
        modelBuilder.ApplyConfiguration(new BankLinkPaymentConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseMaintenanceRequestConfiguration());
        modelBuilder.Entity<BankLinkPayment>(BankLinkPaymentConfiguration.ConfigureLeaseMaintenanceRequestIdForeignKey);
        modelBuilder.Entity<RentLedgerEntry>(RentLedgerEntryConfiguration.ConfigureOrgIdIndex);
        modelBuilder.ApplyConfiguration(new RentLedgerComponentConfiguration());
        modelBuilder.ApplyConfiguration(new TenantPaymentDeclarationConfiguration());
        modelBuilder.ApplyConfiguration(new LeaseCondominiumTermsConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumUnitConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumPeriodConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumStatementConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumExpenseLineConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumInstallmentConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumPriorBalanceConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumPaymentConfiguration());
        modelBuilder.ApplyConfiguration(new TenantCondominiumSettlementConfiguration());
        modelBuilder.ApplyConfiguration(new TenantExpenseAllocationConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumImportBatchConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumImportHouseConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumImportYearConfiguration());
        modelBuilder.ApplyConfiguration(new CondominiumStatementProposalConfiguration());
        modelBuilder.ApplyConfiguration(new RentBankMovementConfiguration());

        // ConcordatoFacile integration (NF-02, NF-03, ADR-004)
        modelBuilder.ApplyConfiguration(new ConcordatoFacilePurchaseConfiguration());
        modelBuilder.ApplyConfiguration(new ConcordatoFacileGiftConfiguration());
        modelBuilder.ApplyConfiguration(new CfSimulatedPracticeConfiguration());
        modelBuilder.ApplyConfiguration(new OrgConfiguration());
        modelBuilder.ApplyConfiguration(new OrgSlugAliasConfiguration());

        // Operator documents of the public site (BK-14, A3-21): immutable versions numbered per org and kind. The unique
        // index is the guarantee of the numbering under concurrent publishes (23505, the loser takes the next number).
        modelBuilder.ApplyConfiguration(new OrgSiteDocumentConfiguration());
        modelBuilder.Entity<Org>(OrgConfiguration.ConfigureCustomDomainIndex);
        modelBuilder.ApplyConfiguration(new PlatformInvoiceConfiguration());
        modelBuilder.Entity<Property>(PropertyConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<Booking>(BookingConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<LeaseContract>(LeaseContractConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<Payment>(PaymentConfiguration.ConfigureOrgIdIndex);
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.Entity<Guest>(GuestConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<PropertyDocument>(PropertyDocumentConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<OtaIntegration>(OtaIntegrationConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<PricingAdapterConfig>(PricingAdapterConfigConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<PricingHistory>(PricingHistoryConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<AlloggiatiWebReport>(AlloggiatiWebReportConfiguration.ConfigureOrgIdIndex);
        modelBuilder.Entity<Property>(PropertyConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<Booking>(BookingConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<LeaseContract>(LeaseContractConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<Payment>(PaymentConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.ApplyConfiguration(new PropertyFiscalYearConfiguration());
        modelBuilder.Entity<Payment>(PaymentConfiguration.ConfigureOtaWithholdingTax);
        modelBuilder.Entity<User>(UserConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<Guest>(GuestConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<PropertyDocument>(PropertyDocumentConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<OtaIntegration>(OtaIntegrationConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<PricingAdapterConfig>(PricingAdapterConfigConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<PricingHistory>(PricingHistoryConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.Entity<AlloggiatiWebReport>(AlloggiatiWebReportConfiguration.ConfigureOrgIdForeignKey);
        modelBuilder.ApplyConfiguration(new GuestCheckInSessionOrgConfiguration());
        modelBuilder.ApplyConfiguration(new AppContextEntityConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new UserContextMembershipConfiguration());

        // ─── Org membership (AM-01) ─────────────────────────────────────────────
        modelBuilder.ApplyConfiguration(new OrgMemberConfiguration());

        // ─── Org invitations (AM-02) ────────────────────────────────────────────
        modelBuilder.ApplyConfiguration(new OrgInvitationConfiguration());

        // ─── The properties an invitation carries (AM-02c) ──────────────────────
        modelBuilder.ApplyConfiguration(new OrgInvitationPropertyConfiguration());

        // ─── Property scope of the org members (AM-03) ──────────────────────────
        modelBuilder.ApplyConfiguration(new PropertyMemberAccessConfiguration());

        // ─── Activity log of the org (AM-02b) ───────────────────────────────────────────────────────────────
        modelBuilder.ApplyConfiguration(new OrgActivityEntryConfiguration());
        modelBuilder.Entity<Property>(PropertyConfiguration.ConfigureMemberChargeProperty);
        modelBuilder.ApplyConfiguration(new ConsentRecordConfiguration());

        // ─── Signup attribution (SE-03 / A8-03) ─────────────────────────────────
        modelBuilder.ApplyConfiguration(new SignupAttributionConfiguration());

        // SEO funnel events (SE-04): platform data without an org or a person; the report groups by comune over a window,
        // the nightly retention deletes by date.
        modelBuilder.ApplyConfiguration(new SeoEventConfiguration());
        modelBuilder.ApplyConfiguration(new SupplierProfileConfiguration());

        // One profile per email (SU-14): the unique index on lower(btrim("Email")) is an expression index that EF cannot
        // model; it is created by the migration SupplierProfileEmailUnique (see SupplierProfileEmailIndex).

        modelBuilder.ApplyConfiguration(new SupplierAvailabilityConfiguration());

        // SP-02: the supplier's service catalog. Children of the supplier profile (cascade, like the availability days: the
        // repair moves them to the keeper before it deletes a duplicate profile). The slug is unique among the services
        // that are not deleted, so a deleted one frees it; xmin is the concurrency token. The checks mirror
        // SupplierServiceListingRules (looser where the rule is a product bound, e.g. the shortest duration).
        modelBuilder.ApplyConfiguration(new SupplierServiceListingConfiguration());

        // SP-03: the supplier's agenda. Children of the supplier profile in cascade, like the availability days and the
        // catalog: the repair moves them to the keeper before it deletes a duplicate profile. The checks mirror
        // SupplierAgendaRules / SupplierAgendaLimits (the rules refuse first; the database is the last guard).
        modelBuilder.ApplyConfiguration(new SupplierWorkingHoursConfiguration());

        modelBuilder.ApplyConfiguration(new SupplierTimeOffConfiguration());

        modelBuilder.ApplyConfiguration(new SupplierBusyWindowConfiguration());

        modelBuilder.ApplyConfiguration(new SupplierFreeCalendarEventConfiguration());

        modelBuilder.ApplyConfiguration(new SupplierSettingsConfiguration());

        // SP-REL: the events of the reliability of a supplier, a child of its profile like the agenda.
        modelBuilder.ApplyConfiguration(new SupplierReliabilityEventConfiguration());

        // SP-13a: the content of the supplier's showcase (draft, published, previous). A child of the supplier profile in cascade, like
        // the settings; xmin is the concurrency token.
        modelBuilder.ApplyConfiguration(new SupplierShowcaseContentConfiguration());
        modelBuilder.ApplyConfiguration(new SupplierShowcaseMediaConfiguration());
        modelBuilder.ApplyConfiguration(new SupplierInviteRecordConfiguration());
        modelBuilder.ApplyConfiguration(new SupplierAdminAuditEntryConfiguration());

        // ─── Host invites (invitation of new independent hosts) ──────────────────
        modelBuilder.ApplyConfiguration(new HostInviteRecordConfiguration());

        // Audit trail and source of the daily quota: read by host org and time window. No foreign key: it outlives a purged invite.
        modelBuilder.ApplyConfiguration(new HostInviteAuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new ServiceRequestConfiguration());

        // The JSON columns are added to a table that already has rows: the database gives those the empty list (an empty string
        // is not valid jsonb), the entity gives it to the new ones.
        modelBuilder.ApplyConfiguration(new ServiceRequestPricingConfiguration());
        modelBuilder.Entity<ServiceRequest>(ServiceRequestConfiguration.ConfigureChecksMirrorServiceRequestLimits);

        // ─── Booking from the public showcase of a supplier (SP-10, decision D34 revised) ─────────────────────────────
        // A showcase request belongs to the supplier (OrgId = the supplier org), has no property and no booking, and carries its
        // customer, its public code and the place of the work. The database keeps that true: the context, the source and these
        // columns go together (PropertyId and BookingId are nullable only because of it). The host contexts keep what they had
        // (a property, never a customer, a code or a place of their own); nothing is asked of BookingId there, which older
        // requests leave empty. The check holds for every row that exists before this migration.
        modelBuilder.ApplyConfiguration(new ServiceRequestShowcaseConfiguration());

        // ─── Backup suppliers (SP-BKP) ───────────────────────────────────────────────────────────────────────────────
        // A request and the backups it was sent to with it are tied by a group; the group's windows decide who has the job.
        modelBuilder.ApplyConfiguration(new ServiceRequestGroupConfiguration());
        modelBuilder.ApplyConfiguration(new ServiceRequestGroupMembershipConfiguration());

        // The lease a long-rent request is about (LR-03c): a link, set only when the landlord asks from the sheet of the contract.
        modelBuilder.ApplyConfiguration(new ServiceRequestLeaseConfiguration());

        // The VAT of the company of a cleaning recharged to the owner (FIX/BE-CLEANING-VAT): the answer on the request, and what an organization remembers of a company.
        modelBuilder.ApplyConfiguration(new ServiceRequestSupplierVatConfiguration());
        modelBuilder.ApplyConfiguration(new SupplierVatDefaultConfiguration());
        modelBuilder.Entity<Org>(OrgConfiguration.ConfigureSupplierBackupsEnabled);

        // ─── Quote with the customer's approval (SP-04b) ──────────────────────────────────────────────────────────────
        // The offer a supplier makes on a request still waiting for its answer: its columns, and the checks that keep it true.
        modelBuilder.ApplyConfiguration(new ServiceRequestQuoteConfiguration());

        modelBuilder.ApplyConfiguration(new ServiceCustomerConfiguration());

        modelBuilder.ApplyConfiguration(new ShowcaseBookingHoldConfiguration());

        // The request on measure (SP-10b): the inquiry that waits for the e-mail check and the messages of its thread.
        modelBuilder.ApplyConfiguration(new ShowcaseInquiryConfiguration());
        modelBuilder.ApplyConfiguration(new ServiceRequestMessageConfiguration());
        modelBuilder.Entity<SupplierProfile>(SupplierProfileConfiguration.ConfigureTable);

        // One payment per request that is not canceled: the live one. A canceled row (replaced by an offline record, or withdrawn)
        // frees the request, so the index ignores it. The creation paths also take the advisory lock ServiceRequestPayment; this
        // index is what makes a second live payment impossible even if one of them did not.
        modelBuilder.ApplyConfiguration(new ServiceRequestPaymentConfiguration());
        modelBuilder.Entity<SupplierProfile>(SupplierProfileConfiguration.ConfigureTable2);

        // A refund belongs to its payment: restrict, so a payment with refunds is never deleted. The sequence is the n of the
        // idempotency key and is unique within the payment; a Stripe refund and a key belong to one refund each.
        modelBuilder.ApplyConfiguration(new ServiceRequestPaymentRefundConfiguration());

        // ─── The complaint and the dispute about a job paid through CasaZen (SP-15c-c) ─────────────
        // One open dispute per payment (unique partial index); a payment, a request and a supplier with a dispute are never deleted.
        modelBuilder.ApplyConfiguration(new ServicePaymentDisputeConfiguration());
        modelBuilder.ApplyConfiguration(new CalendarBlockConfiguration());
        modelBuilder.Entity<Booking>(BookingConfiguration.ConfigureICalFeedIdIndex);
        modelBuilder.ApplyConfiguration(new PropertyICalFeedConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyICalExportConfiguration());

        // ─── Scheduled change of rental mode (PM-02) ────────────────────────────
        modelBuilder.ApplyConfiguration(new PropertyModeChangeConfiguration());
        modelBuilder.Entity<PropertyICalExport>(PropertyICalExportConfiguration.ConfigureExportTokenIndex);
        modelBuilder.ApplyConfiguration(new ApprovalQueueItemConfiguration());
        modelBuilder.Entity<AppContextEntity>(AppContextEntityConfiguration.ConfigureSeedData);
        modelBuilder.Entity<Role>(RoleConfiguration.ConfigureSeedData);
        modelBuilder.Entity<RolePermission>(RolePermissionConfiguration.ConfigureSeedData);
        modelBuilder.Entity<AppContextEntity>(AppContextEntityConfiguration.ConfigureSeedData2);
        modelBuilder.Entity<Role>(RoleConfiguration.ConfigureSeedData2);
        modelBuilder.Entity<RolePermission>(RolePermissionConfiguration.ConfigureSeedData2);

        // Guest check-in session (US-020 / #296)
        modelBuilder.ApplyConfiguration(new GuestCheckInSessionConfiguration());

        // E23/F01/US01/T01, T02 (NF-122): one delivery record per booking for the link of a stay that has no e-mail address; the raw token of the link is encrypted
        // (EncryptedColumns) and the row goes with its booking.
        modelBuilder.ApplyConfiguration(new CheckInLinkDeliveryConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledMessageConfiguration());
        modelBuilder.ApplyConfiguration(new BiometricVerificationLogConfiguration());

        // E70/F01/US01/T02 (NF-194, NF-195): the segments and the evidence of the self-hosted verification; the tenant query filter is applied below, as to every
        // ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new IdentitySegmentSettingConfiguration());
        modelBuilder.ApplyConfiguration(new IdentityReviewEvidenceConfiguration());

        modelBuilder.ApplyConfiguration(new TerritorialRentAgreementConfiguration());
        modelBuilder.ApplyConfiguration(new HighTensionAreaComuneConfiguration());
        modelBuilder.ApplyConfiguration(new ComuneImuChannelConfiguration());
        modelBuilder.ApplyConfiguration(new RegulatoryDataAuditEntryConfiguration());

        // Native host app push tokens (US-025 / #299)
        modelBuilder.ApplyConfiguration(new DeviceRegistrationConfiguration());

        // ─── In-app notifications (UI-12a) ──────────────────────────────────────
        modelBuilder.ApplyConfiguration(new InAppNotificationConfiguration());

        // ─── Popup alerts and the quiet hours of the users (NF-08, NF-22, ADR-025) ──────
        // Both go with the user and with the org (ON DELETE CASCADE); the preferences are unique per (org, user).
        modelBuilder.ApplyConfiguration(new PopupAlertConfiguration());
        modelBuilder.ApplyConfiguration(new UserNotificationPreferencesConfiguration());

        // ─── What each user chose for each kind of event, and the email slot of a conversation (E05/F03, NF-65) ──────
        // Both go with the user and with the org (ON DELETE CASCADE); unique per (org, user, event type) and per (org, user, conversation).
        modelBuilder.ApplyConfiguration(new UserNotificationChannelSettingConfiguration());
        modelBuilder.ApplyConfiguration(new NotificationEmailSlotConfiguration());

        // ─── Verified reviews (DB-12a) ──────────────────────────────────────────
        // A review goes with its booking and its property (a property is soft-deleted, so in practice it stays for good): the host has no
        // way to delete one, and no foreign key stops the platform's own cleanups (the reset of the demo deletes bookings in bulk).
        modelBuilder.ApplyConfiguration(new ReviewConfiguration());

        // The report goes with its review. One per review: the second one loses the race on UIX_ReviewReports_ReviewId (23505, 409).
        // Two staff users deciding the same report at once: the state is the concurrency token, the second decision is a 409.
        modelBuilder.ApplyConfiguration(new ReviewReportConfiguration());

        // The mark of the email asked by the host, once per booking: it goes with the booking.
        modelBuilder.ApplyConfiguration(new ReviewInviteConfiguration());

        // ─── Messages between the guest and the host (DB-14a) ───────────────────
        // A thread goes with its booking (and its property, which is soft-deleted, so in practice it stays): the reset of the demo deletes
        // bookings in bulk and the erasure of a guest deletes its threads, so no foreign key may stop either. The messages go with the thread.
        // Two first messages at the same moment: the loser gets 23505 on UIX_GuestThreads_BookingId and reads the thread the winner made.
        modelBuilder.ApplyConfiguration(new GuestThreadConfiguration());

        // A retried send with the same key loses the race on UIX_GuestMessages_ThreadId_IdempotencyKey (23505) and reads the first message.
        modelBuilder.ApplyConfiguration(new GuestMessageConfiguration());
        StorefrontChatConfiguration.Configure(modelBuilder);

        // ─── The OTA conversations, copied from the channel manager (E05/F01/US01/T01, ADR-032) ───────────────
        // One thread per Octorate thread and organization (UIX_OtaMessageThreads_OrgId_OctorateThreadId), one message per Octorate message
        // (UIX_OtaMessages_OrgId_OctorateMessageId) and per key of the sender's screen in a thread; the words are encrypted (EncryptedColumns).
        // The checkpoints of the sync are one row per organization and accommodation; the templates are unique per organization, name and language.
        modelBuilder.ApplyConfiguration(new OtaMessageThreadConfiguration());
        modelBuilder.ApplyConfiguration(new OtaMessageConfiguration());
        modelBuilder.ApplyConfiguration(new OtaMessageSyncStateConfiguration());
        modelBuilder.ApplyConfiguration(new MessageTemplateConfiguration());

        // The reviews of the channels (E05/F06/US01/T01, NF-69): one row per organization, booking and channel (UIX_OtaReviews_OrgId_BookingId_Channel); the words of the
        // guest, the reply and the review of the host are encrypted (EncryptedColumns); xmin is the concurrency token, so a reply is saved, and sent, once.
        modelBuilder.ApplyConfiguration(new OtaReviewConfiguration());

        // ─── The AI assistant (E05/F05/US01/T01, NF-66, NF-67, ADR-032) ───────────────
        // The free text of a host (the entries of the knowledge base, the lessons, the response templates) is encrypted by EncryptedColumns; the decision log holds ids
        // and codes, never text. One rules row per organization, one exceptions row per property, one situation row per organization (or property) and situation,
        // and one decision per incoming message (unique indexes). The rows of a property go with it (cascade) but for the decision log, which stays when its property is
        // removed (SET NULL: it is the audit of what was sent to a guest); every OrgId foreign key restricts.
        modelBuilder.ApplyConfiguration(new PropertyKnowledgeEntryConfiguration());
        modelBuilder.ApplyConfiguration(new OrgAssistantRulesConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyAssistantSettingsConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantSituationSettingConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantLessonConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantDecisionLogConfiguration());
        modelBuilder.ApplyConfiguration(new AssistantTrustProposalConfiguration());
        modelBuilder.ApplyConfiguration(new RoutineExecutionConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxEventConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateConnectionConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateListingMappingConfiguration());

        // The import of existing OTA inventory (D6, E02/F01/US03): one current intent for each property (partial unique indexes), batches with their
        // channels and the external items done for each connection; every row carries its org (restrict) and the tenant filter applies to all.
        modelBuilder.ApplyConfiguration(new OctorateInventoryIntentConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateInventoryImportBatchConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateInventoryImportChannelConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateInventoryImportItemConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateOAuthStateConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateBookingSyncConfiguration());

        // ─── Invoice data and issuer of the documents (epic E06, NF-81, NF-82) ───────────────────────────────
        // The invoice data of a booking: one row for each booking (unique BookingId), gone with the booking (cascade); the personal columns are
        // encrypted by EncryptedColumns (no length) and xmin is the concurrency token. The tenant query filter is applied below, as to every
        // ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new BookingInvoiceDataConfiguration());

        // The profile of the issuer of an org (unique OrgId) and the single issuer of a booking (unique BookingId, cascade with the booking):
        // the database itself refuses a second one of either (FIS-02, NF-82).
        modelBuilder.ApplyConfiguration(new OrgFiscalIssuerProfileConfiguration());
        modelBuilder.ApplyConfiguration(new BookingDocumentIssuerConfiguration());

        // The fiscal documents (NF-83, NF-84, E06/F01/US02/T01): issued by CasaZen or imported from Octorate, kept ten years (no cascade from the
        // booking, the guest or another document reaches them), numbered without duplicates per org and series, an Octorate document imported once;
        // their lines (cascade with the document) and the counter of the numbers (one row per org and series).
        modelBuilder.ApplyConfiguration(new FiscalDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new FiscalDocumentLineConfiguration());
        modelBuilder.ApplyConfiguration(new FiscalNumberSequenceConfiguration());

        // How the package of the accountant is sent (NF-101, E15/F01/US02/T01): one row for each org (unique OrgId), the address encrypted by EncryptedColumns, the day, the folders and
        // the result bounded by check constraints. The tenant query filter is applied below, as to every ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new AccountantPackageSettingsConfiguration());

        // The owner accounting (NF-109, NF-110, E06/F05/US02): the rule sets of the agency and the effective-dated fiscal profiles of the owners.
        modelBuilder.ApplyConfiguration(new OwnerAccountingPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerFiscalProfileConfiguration());

        // The versioned mandates of the owners and the commission rules of the channels (E13/F01/US01/T01).
        modelBuilder.ApplyConfiguration(new OwnerMandateConfiguration());
        modelBuilder.ApplyConfiguration(new OrgChannelCommissionRuleConfiguration());

        // Who holds the account of a listing on a portal (E14/F01/US01/T01, ⚖ I10): one row for each property and portal, gone with the property.
        modelBuilder.ApplyConfiguration(new PropertyChannelAccountHolderConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerLedgerEntryConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerStatementVersionConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerClosePeriodConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerChargeConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerFxRateConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerExternalDocumentReferenceConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerPayoutConfiguration());

        // The payouts a wire batch pays (E16/F01/US01/T01): a payout is in one batch only (unique on the payout), the batch holds its rows.
        modelBuilder.ApplyConfiguration(new WireBatchPayoutConfiguration());

        // The owner portal operations (E07/F07/US01, NF-111). The tenant query filter is applied below, to every ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new OwnerStayRuleConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerStayRequestConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerDocumentVersionConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerDocumentDownloadConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerPortalContactConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerConversationConfiguration());
        modelBuilder.ApplyConfiguration(new OwnerMessageConfiguration());
        modelBuilder.ApplyConfiguration(new TaxObligationEvidenceConfiguration());
        modelBuilder.ApplyConfiguration(new TaxReportingDatasetConfiguration());

        // The decisions about the regime of the stays of a company tenant (E18/F01/US03/T01, ⚖ I7): the tenant query filter is applied below, to every ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new ShortLetTaxDecisionConfiguration());
        modelBuilder.ApplyConfiguration(new AgencyInvoiceConfiguration());
        modelBuilder.Entity<Payment>(PaymentConfiguration.ConfigureOrgIdIndex2);

        // ─── Check-in to Octorate (NF-41, NF-45, E03/F01/US01/T03) ───────────────
        // The guests sent (one per booking and position) and the states of a stay (one per booking and state); the tenant query filter is
        // applied below, to every ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new StayGuestOctorateLinkConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateStayStateSyncConfiguration());
        modelBuilder.ApplyConfiguration(new InternalActivityConfiguration());

        // ─── The cleaning at check-out (E24/F01/US01, NF-120) ────────────────────────
        // The default cleaner of a property with the price and the time agreed (one for each property), and the automatic cleaning of a stay (one for each
        // stay: the key of the idempotency of the automation). The tenant query filter is applied below, to every ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new PropertyCleaningSetupConfiguration());
        modelBuilder.ApplyConfiguration(new StayCleaningConfiguration());
        modelBuilder.ApplyConfiguration(new WizardDraftConfiguration());
        modelBuilder.ApplyConfiguration(new TouristTaxRemittanceConfiguration());

        // ─── Tourist tax per guest (NF-46, ADR-037) ──────────────────────────────
        // Reference data (exemption reasons, seeded; channel rules, never seeded), the host-declared rates and the confirmed stay record with
        // its lines. The tenant query filter is applied below, to every ITenantOwned entity. Since E25/F01/US01/T01 (NF-119) the record also carries the
        // collection of the tax (what the guest paid online, the intent, the refunds), with its unique intent and its check of the refunds: never a Payment row.
        modelBuilder.ApplyConfiguration(new TouristTaxExemptionReasonConfiguration());
        modelBuilder.ApplyConfiguration(new TouristTaxChannelRuleConfiguration());
        modelBuilder.ApplyConfiguration(new OrgTouristTaxRateConfiguration());
        modelBuilder.ApplyConfiguration(new StayTouristTaxConfiguration());
        modelBuilder.ApplyConfiguration(new StayTouristTaxGuestConfiguration());

        // ─── ISTAT region table (E21/F01/US01/T01, decision O6) ──────────────────
        // Platform reference data, seeded with every row «da verificare» (coverage Unknown) and edited only by the staff, each change in the audit trail. One row
        // for each region and for each autonomous province (two partial unique indexes), never without its https source: the database holds the checks.
        modelBuilder.ApplyConfiguration(new IstatRegionRuleConfiguration());
        modelBuilder.ApplyConfiguration(new MarketplacePortalConfiguration());
        modelBuilder.ApplyConfiguration(new MarketplacePartnerConfiguration());
        modelBuilder.ApplyConfiguration(new MarketplacePartnerTextConfiguration());
        modelBuilder.ApplyConfiguration(new MarketplaceActivationConfiguration());

        // ─── Activation of CasaZen with Octorate (NF-104..NF-107, E10/F01/US01/T01) ─────────────────────────────
        // The host's intents and the activation facts only: the state of a part or of a flow is derived on read. One state for each org and
        // part, one activation for each property and flow, one panel step for each org, property, kind and channel (two NULLs are equal),
        // one open «Do it for me» request for each org, part and property, one pilot membership and one data row for each property. The
        // property-bound rows go with their property (cascade); every row carries its org (restrict) and the tenant filter applies to all.
        modelBuilder.ApplyConfiguration(new OnboardingPartStateConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyFlowActivationConfiguration());
        modelBuilder.ApplyConfiguration(new OnboardingPanelStepConfiguration());
        modelBuilder.ApplyConfiguration(new OnboardingAssistRequestConfiguration());
        modelBuilder.ApplyConfiguration(new OnboardingPilotMemberConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyOnboardingDataConfiguration());

        // The support vault (E20/F03/US01/T01): one credential for each org, property and kind (two NULLs are equal), the openings of each request,
        // one ISTAT form for each property. The rows follow their org and property (cascade): the vault never outlives nor blocks their erasure.
        modelBuilder.ApplyConfiguration(new SupportVaultSecretConfiguration());
        modelBuilder.ApplyConfiguration(new SupportVaultAccessConfiguration());
        modelBuilder.ApplyConfiguration(new PropertyIstatCredentialsConfiguration());

        // The key an agency gives its owners (E10/F02/US02/T01): unique in the org, for the owners that have one.
        modelBuilder.ApplyConfiguration(new PropertyOwnerConfiguration());

        // ─── Per-date prices and restrictions (NF-36, E08/F05/US01/T01) ─────────────────────────────────────────────
        // The price and the rules of a night (one row per property and stay date, the values bounded by a check constraint), and the changes of
        // the calendar sent to the channel manager (the process the host follows and its chunks). Each row carries its org (restrict) and the
        // tenant query filter applies to all of them, below, as to every ITenantOwned entity; the rows go with their property (cascade).
        modelBuilder.ApplyConfiguration(new PropertyDayRuleConfiguration());
        modelBuilder.ApplyConfiguration(new CalendarChangeProcessConfiguration());
        modelBuilder.ApplyConfiguration(new CalendarChangeChunkConfiguration());

        // The connection of a site to the org (S022, PLG-BE-CONNECT, site integration plan §10): the one-time codes and the credentials of the plugin, hashes
        // only, with their org (cascade); one active credential for each org and site. The tenant query filter applies to both, below.
        modelBuilder.ApplyConfiguration(new SiteConnectionCodeConfiguration());
        modelBuilder.ApplyConfiguration(new SiteConnectionConfiguration());

        // PC-05, A2-18: soft-deleted properties never appear in a normal read. A separate named filter (not the
        // tenant one) so it composes independently: IgnoreQueryFilters([TenantQueryFilter]) (entitlement counts,
        // admin cross-org reads) still excludes deleted properties, and IgnoreQueryFilters([SoftDeleteQueryFilter])
        // (fiscal/compliance reporting) still respects tenant isolation.
        modelBuilder.Entity<Property>().HasQueryFilter(SoftDeleteQueryFilter, p => !p.IsDeleted);

        // UI-13a: the stored search keys and their indexes (global search, docs/runbooks/global-search.md).
        SearchKeyModel.Configure(modelBuilder);

        modelBuilder.ApplyConfiguration(new GuestHostExtraOrderConfiguration());
        modelBuilder.ApplyConfiguration(new GuestHostExtraConfiguration());
        modelBuilder.Entity<TrustedSupplier>()
            .HasQueryFilter(TrustedSupplierActiveFilter, supplier => supplier.RemovedAt == null);
        modelBuilder.ApplyConfiguration(new GuestCrmProfileConfiguration());
        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>Key of the global tenant query filter, for <c>IgnoreQueryFilters([TenantQueryFilter])</c>.</summary>
    public const string TenantQueryFilter = "Tenant";
    public const string TrustedSupplierActiveFilter = "ActiveTrustedSupplier";

    /// <summary>
    /// Key of the global soft-delete filter on <see cref="Property"/> (PC-05), for
    /// <c>IgnoreQueryFilters([SoftDeleteQueryFilter])</c> where a deleted property's historical data must still be
    /// reachable (fiscal reports, compliance exports).
    /// </summary>
    public const string SoftDeleteQueryFilter = "SoftDelete";

    private static readonly MethodInfo ApplyTenantQueryFilterMethod = typeof(AppDbContext)
        .GetMethod(nameof(ApplyTenantQueryFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// Global tenant query filter (AC7, TN-2): every read of an <see cref="ITenantOwned"/> entity is scoped
    /// to the caller's OrgId. Fail-closed when an authenticated caller has no org; disabled for
    /// anonymous/system contexts (public endpoints, background jobs, design-time, unit tests), where
    /// every query filters explicitly. Registered for every ITenantOwned entity of the model, so a new
    /// tenant entity cannot be left unfiltered by forgetting a line here.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.BaseType is null
                && !entityType.IsOwned()
                && typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                ApplyTenantQueryFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    // The lambda reads _tenant through this context instance, so EF re-evaluates it for every query.
    private void ApplyTenantQueryFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantOwned =>
        modelBuilder.Entity<TEntity>()
            .HasQueryFilter(TenantQueryFilter, e => !_tenant.FilterEnabled || e.OrgId == _tenant.OrgId);
}
