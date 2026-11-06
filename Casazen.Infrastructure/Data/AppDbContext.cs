using Casazen.Infrastructure.Data.Configurations;
using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.SiteProfile;
using Casazen.Core.Suppliers;
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
    public DbSet<WithholdingLog> WithholdingLogs { get; set; } = null!;
    public DbSet<F24Record> F24Records { get; set; } = null!;
    public DbSet<OwnerInvitation> OwnerInvitations { get; set; } = null!;
    public DbSet<Booking> Bookings { get; set; } = null!;
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

    // Official ISTAT list of the comuni and the log of its imports (SU-04)
    public DbSet<Comune> Comuni { get; set; } = null!;
    public DbSet<ComuneImport> ComuneImports { get; set; } = null!;
    public DbSet<OfficialSourceFetch> OfficialSourceFetches { get; set; } = null!;
    public DbSet<ComuneOfficialProfile> ComuneOfficialProfiles { get; set; } = null!;
    public DbSet<ComuneOfficialProfileVersion> ComuneOfficialProfileVersions { get; set; } = null!;

    // Swiss municipalities from Ufficio federale di statistica (NF-02)
    public DbSet<SwissMunicipality> SwissMunicipalities { get; set; } = null!;

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

    /// <summary>
    /// Internal activities performed by staff on properties (NF-22): cleaning, maintenance, check-in assistance.
    /// Staff sees only activities within their perimeter (PropertyMemberAccess).
    /// </summary>
    public DbSet<InternalActivity> InternalActivities { get; set; } = null!;

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

    // Guest identity document images for OCR (NF-10, ADR-013-R3)
    public DbSet<DocumentImage> DocumentImages { get; set; } = null!;

    // Biometric verification settings and audit logs (NF-11, ADR-013)
    public DbSet<BiometricSettings> BiometricSettings { get; set; } = null!;
    public DbSet<BiometricVerificationLog> BiometricVerificationLogs { get; set; } = null!;

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
    public DbSet<LeaseRegistrationAuthorization> LeaseRegistrationAuthorizations { get; set; } = null!;
    public DbSet<LeaseSigner> LeaseSigners { get; set; } = null!;
    public DbSet<RentSchedule> RentSchedules { get; set; } = null!;
    public DbSet<RentLedgerEntry> RentLedgerEntries { get; set; } = null!;
    public DbSet<RentBankMovement> RentBankMovements { get; set; } = null!;

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

    // Marketing campaigns and segments (NF-24, ADR-014)
    public DbSet<GuestSegment> GuestSegments { get; set; } = null!;
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
    public DbSet<GuestThread> GuestThreads { get; set; } = null!;
    public DbSet<GuestMessage> GuestMessages { get; set; } = null!;

    // The copy of the conversations of the guests with the channels (Airbnb, Booking.com, Vrbo, Expedia), read from the channel manager: the threads,
    // their messages (the words encrypted), the checkpoints of the sync of each listing, and the message templates of the hosts (E05/F01/US01/T01, ADR-032)
    public DbSet<OtaMessageThread> OtaMessageThreads { get; set; } = null!;
    public DbSet<OtaMessage> OtaMessages { get; set; } = null!;
    public DbSet<OtaMessageSyncState> OtaMessageSyncStates { get; set; } = null!;
    public DbSet<MessageTemplate> MessageTemplates { get; set; } = null!;

    // The tourist tax a host paid to a comune and the receipt of the payment: the «paid» half of the summary per comune (NF-100, REP-05)
    public DbSet<TouristTaxRemittance> TouristTaxRemittances { get; set; } = null!;

    // The tourist tax per guest (NF-46, ADR-037, E03/F01/US07/T01): the exemption reasons and the channel rules (platform reference data),
    // the rate a host declares for a comune with no official one and the confirmed tax of a stay (tenant-owned; its lines are reached only
    // through it)
    public DbSet<TouristTaxExemptionReason> TouristTaxExemptionReasons { get; set; } = null!;
    public DbSet<TouristTaxChannelRule> TouristTaxChannelRules { get; set; } = null!;
    public DbSet<OrgTouristTaxRate> OrgTouristTaxRates { get; set; } = null!;
    public DbSet<StayTouristTax> StayTouristTaxes { get; set; } = null!;

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

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // FD-06: every DateTime is UTC in and out of PostgreSQL timestamptz columns.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeValueConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeValueConverter>();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, DataProtectionModelCacheKeyFactory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Property>()
            .HasMany(p => p.Bookings)
            .WithOne(b => b.Property)
            .HasForeignKey(b => b.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Property>()
            .HasMany(p => p.OtaIntegrations)
            .WithOne(o => o.Property)
            .HasForeignKey(o => o.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Booking>()
            .HasMany(b => b.Payments)
            .WithOne(p => p.Booking)
            .HasForeignKey(p => p.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Guest>()
            .HasMany(g => g.Bookings)
            .WithOne(b => b.Guest)
            .HasForeignKey(b => b.GuestId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AlloggiatiWebReport>()
            .HasOne(r => r.Booking)
            .WithMany(b => b.AlloggiatiWebReports)
            .HasForeignKey(r => r.BookingId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AlloggiatiWebReport>()
            .HasOne(r => r.Guest)
            .WithMany(g => g.AlloggiatiWebReports)
            .HasForeignKey(r => r.GuestId)
            .OnDelete(DeleteBehavior.Restrict);

        // CO-11: "Inviato" only with a real receipt, never as a simulation.
        modelBuilder.Entity<AlloggiatiWebReport>()
            .ToTable(t => t.HasCheckConstraint(
                "CK_AlloggiatiWebReports_SentRequiresReceipt",
                $"\"Status\" <> {(int)AlloggiatiWebStatus.Inviato} OR btrim(coalesce(\"ConfirmationNumber\", '')) <> ''"));

        // AW-R05: the daily receipt of the account that covers the communication; a receipt removed at the end of its retention leaves
        // the communication without it (SET NULL), never removes it.
        modelBuilder.Entity<AlloggiatiWebReport>()
            .HasOne<AlloggiatiReceipt>()
            .WithMany()
            .HasForeignKey(r => r.ReceiptId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // CO-10: the alert stages of a stay go with it.
        modelBuilder.Entity<StayAlertState>()
            .HasOne(s => s.Booking)
            .WithMany()
            .HasForeignKey(s => s.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // CO-17: the check-out of a stay goes with it; its cleaning request survives it as a plain request.
        modelBuilder.ApplyConfiguration(new StayCheckoutConfiguration());

        // CO-20: the CIN alert stage of a property goes with it.
        modelBuilder.Entity<CinAlertState>()
            .HasOne(s => s.Property)
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

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

        modelBuilder.Entity<AlloggiatiCodeEntry>()
            .HasOne(e => e.Import)
            .WithMany()
            .HasForeignKey(e => e.ImportId)
            .OnDelete(DeleteBehavior.Restrict);

        // SU-04: official ISTAT list of the comuni. The ISTAT code is the key; the cadastral code is unique among the active
        // comuni only (a comune that changes province gets a new ISTAT code and keeps its cadastral code, so the old row,
        // deactivated, and the new one coexist).
        modelBuilder.ApplyConfiguration(new ComuneConfiguration());
        modelBuilder.ApplyConfiguration(new ComuneImportConfiguration());
        modelBuilder.ApplyConfiguration(new OfficialSourceFetchConfiguration());
        modelBuilder.ApplyConfiguration(new ComuneOfficialProfileConfiguration());
        modelBuilder.ApplyConfiguration(new ComuneOfficialProfileVersionConfiguration(Database.IsNpgsql()));

        // CO-14: one set of Alloggiati Web credentials per property, going with it; tenant row (TN-2).
        modelBuilder.ApplyConfiguration(new PropertyQuesturaCredentialsConfiguration());

        // ADR-029, ADR-015 (E03/F01/US05/T01): the sender of each property (never two), the attempts of the Alloggiati transmission (at
        // most one open per booking) and the daily receipts of a Questura account (one per org, account, day and mode); tenant rows (TN-2).
        modelBuilder.ApplyConfiguration(new PropertyComplianceSenderConfiguration());
        modelBuilder.ApplyConfiguration(new AlloggiatiSendAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new AlloggiatiReceiptConfiguration());

        // "Le mie prenotazioni" finds a booking by the org of the site and its code (BK-11).
        modelBuilder.Entity<Booking>()
            .HasIndex(b => new { b.OrgId, b.BookingCode })
            .IsUnique();

        // A reservation of Octorate is one booking in an org (E02/F02/US01/T01, ADR-033-R1): the key of the import, whoever imports it and
        // however many times. Only the rows that have an Octorate id are in it; the others (null) are as many as there are bookings.
        modelBuilder.Entity<Booking>()
            .HasIndex(b => new { b.OrgId, b.OctorateReservationId })
            .IsUnique()
            .HasFilter("\"OctorateReservationId\" IS NOT NULL")
            .HasDatabaseName(Booking.OctorateReservationIndexName);

        // GPS coordinates (PC-06, A2-33): 6 decimals are about 0.1 m, the old 2 decimals placed a house up to a kilometre
        // away on the map. numeric(9,6) holds -999.999999..999.999999: the API accepts only -90..90 and -180..180.
        modelBuilder.Entity<Property>()
            .Property(p => p.Latitude)
            .HasPrecision(9, PropertyAddress.CoordinateScale);

        modelBuilder.Entity<Property>()
            .Property(p => p.Longitude)
            .HasPrecision(9, PropertyAddress.CoordinateScale);

        // DB-03: the stay rules the public quote and checkout read (minimum stay, weekend surcharge). The host forms and the
        // property service validate them; the constraints keep any other writer honest. Both columns are nullable or 0 for
        // every row that existed before them, so the migration changes no behavior (docs/runbooks/direct-booking.md § 11).
        modelBuilder.Entity<Property>().ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_Properties_MinNights",
                $"\"MinNights\" IS NULL OR \"MinNights\" BETWEEN {PropertyStayRules.MinMinNights} AND {PropertyStayRules.MaxMinNights}");
            t.HasCheckConstraint(
                "CK_Properties_WeekendSurchargePercent",
                $"\"WeekendSurchargePercent\" BETWEEN 0 AND {(int)PropertyStayRules.MaxWeekendSurchargePercent}");
        });

        // SR-07a: the host's switch «Mostra la casa sul sito» starts true — in the migration, so every row that existed before the
        // column keeps being on the site — and it is one more condition of PublicListing.IsPublished. The default is for the writers
        // that do not know the column (the old release during a deploy, a raw INSERT); EF itself always writes the value of the
        // entity: left to generate it, EF would take an explicit false for «not set» and the insert would write the default (true)
        // in its place, putting back on the site a property its host hid.
        // The other fields of the wizard (type, beds, area, headline, times, rules, fee) are nullable: null = never said.
        modelBuilder.Entity<Property>()
            .Property(p => p.PublishedOnSite)
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // DB-11a: the site profile (floor, renovation year, energy class and index, kind of place, children, latest arrival, video and tour
        // addresses, the JSON of the texts and of the photo alt texts). Every column is nullable: null = the host never said, which is
        // every property that existed before them. The host API validates (SitePropertyProfileRules); the constraints keep any other
        // writer honest. The ranges repeat the constants of SitePropertyProfileLimits.
        modelBuilder.Entity<Property>().ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_Properties_FloorLevel",
                $"\"FloorLevel\" IS NULL OR \"FloorLevel\" BETWEEN {SitePropertyProfileLimits.MinFloorLevel} AND {SitePropertyProfileLimits.MaxFloorLevel}");
            t.HasCheckConstraint(
                "CK_Properties_RenovatedYear",
                $"\"RenovatedYear\" IS NULL OR \"RenovatedYear\" >= {SitePropertyProfileLimits.MinRenovatedYear}");
            t.HasCheckConstraint(
                "CK_Properties_EnergyClass",
                "\"EnergyClass\" IS NULL OR \"EnergyClass\" IN ('A4', 'A3', 'A2', 'A1', 'A', 'B', 'C', 'D', 'E', 'F', 'G')");
            t.HasCheckConstraint(
                "CK_Properties_EnergyIndex",
                $"\"EnergyIndex\" IS NULL OR (\"EnergyClass\" IS NOT NULL AND \"EnergyIndex\" >= 0 AND \"EnergyIndex\" <= {SitePropertyProfileLimits.MaxEnergyIndex})");
        });

        // Indexes
        modelBuilder.Entity<Property>().HasIndex(p => p.OwnerId);

        // SU-04: the comune chosen from the official list; the region follows it.
        modelBuilder.Entity<Property>().HasIndex(p => p.ComuneIstatCode);

        // PM-01: the lists of the host areas filter the properties of an org by rental mode (GET /api/properties?mode=), and
        // the jobs and the public site read the short-rent ones; the column is stored as an integer (append only) and the
        // existing rows keep the default 0 = Short (migration AddPropertyRentalMode, docs/runbooks/property-rental-mode.md).
        modelBuilder.Entity<Property>().HasIndex(p => new { p.OrgId, p.RentalMode });

        // Unique address PER ORG and per unit (PC-06, A2-19). Before, the index was global: a host with two apartments in
        // the same building could not create the second, and a host whose address was already used by ANOTHER org got a
        // 409 that revealed a datum of that tenant. "AddressKey" is a stored generated column (shadow property): street,
        // city, postal code and unit with case and runs of spaces ignored, so "Via Roma  1" and "via roma 1" are the same
        // address; it is computed by the database, so every writer of the table is covered and the application never
        // builds the key (never check-then-insert: the loser of two parallel creates gets 23505 on this index).
        // A soft-deleted property (PC-05, IsDeleted) frees its address, so it can be re-created there. A paused one
        // (PC-03, IsPaused) keeps it: pausing is temporary and the property stays the host's.
        modelBuilder.Entity<Property>()
            .Property<string>("AddressKey")
            .HasColumnType("text")
            .HasComputedColumnSql(
                "lower(regexp_replace(btrim(\"Address\"), '\\s+', ' ', 'g')) || '|' || "
                + "lower(regexp_replace(btrim(\"City\"), '\\s+', ' ', 'g')) || '|' || "
                + "lower(btrim(\"PostalCode\")) || '|' || "
                + "lower(regexp_replace(btrim(coalesce(\"Unit\", '')), '\\s+', ' ', 'g'))",
                stored: true);

        modelBuilder.Entity<Property>()
            .HasIndex("OrgId", "AddressKey")
            .IsUnique()
            .HasDatabaseName(PropertyAddress.UniqueIndexName)
            .HasFilter("\"IsActive\" = true AND \"IsDeleted\" = false");

        modelBuilder.Entity<Property>()
            .HasIndex(p => new { p.OrgId, p.Slug })
            .IsUnique()
            // A soft-deleted property (PC-05) frees its slug: SlugExistsInOrgAsync no longer sees it either.
            .HasFilter("\"Slug\" IS NOT NULL AND \"IsDeleted\" = false")
            .HasDatabaseName("UIX_Properties_OrgId_Slug");

        modelBuilder.Entity<Booking>().HasIndex(b => b.PropertyId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.GuestId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.CheckInDate);
        modelBuilder.Entity<Booking>().HasIndex(b => b.Status);
        modelBuilder.Entity<Payment>().HasIndex(p => p.BookingId);

        // Refunds on Stripe (BK-02): one row per Stripe refund, one Stripe request per row (idempotency key).
        modelBuilder.ApplyConfiguration(new PaymentRefundConfiguration());
        modelBuilder.Entity<OtaIntegration>().HasIndex(o => o.PropertyId);

        // Every encrypted column (OTA secrets, iCal import URLs, guest identity documents, Questura credentials) is
        // declared and configured in one place, EncryptedColumns, with the Data Protection value converter
        // (PC-11, CO-14, docs/runbooks/encryption.md).
        if (EncryptionProvider is not null)
            EncryptedColumns.Configure(modelBuilder, EncryptionProvider);

        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => t.City);
        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => new { t.City, t.IsActive, t.EffectiveFrom });
        modelBuilder.Entity<TouristTaxRate>()
            .Property(t => t.VerificationLevel)
            .HasConversion<string>()
            .HasMaxLength(20);
        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => t.IstatCode);
        modelBuilder.Entity<TouristTaxRate>()
            .Property(t => t.CalculationMethod)
            .HasConversion<string>()
            .HasMaxLength(30)
            .HasDefaultValue(TouristTaxCalculationMethod.PerPersonPerNight)
            .HasSentinel((TouristTaxCalculationMethod)(-1));

        modelBuilder.Entity<SeoContentPage>()
            .HasIndex(p => new { p.ComuneCode, p.PageType })
            .IsUnique();

        modelBuilder.Entity<SeoContentPage>()
            .HasIndex(p => p.LegalReviewStatus);

        modelBuilder.Entity<SeoContentRevision>()
            .HasOne(r => r.Page)
            .WithMany(p => p.Revisions)
            .HasForeignKey(r => r.PageId)
            .OnDelete(DeleteBehavior.Cascade);

        // SE-01: the public sees only the approved revision; deleting it makes the page non-public.
        modelBuilder.Entity<SeoContentPage>()
            .HasOne(p => p.PublishedRevision)
            .WithMany()
            .HasForeignKey(p => p.PublishedRevisionId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<SeoContentRevision>()
            .Property(r => r.ContentStatus)
            .HasConversion<string>()
            .HasMaxLength(40)
            .HasDefaultValue(SeoContentStatus.Generated)
            .HasSentinel((SeoContentStatus)(-1));

        modelBuilder.Entity<SeoContentRevision>()
            .HasIndex(r => new { r.PageId, r.GeneratedAt });

        modelBuilder.Entity<SeoContentReviewEvent>()
            .Property(e => e.Action)
            .HasConversion<string>()
            .HasMaxLength(20);

        modelBuilder.Entity<SeoContentReviewEvent>()
            .HasOne<SeoContentPage>()
            .WithMany()
            .HasForeignKey(e => e.PageId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SeoContentReviewEvent>()
            .HasOne<SeoContentRevision>()
            .WithMany()
            .HasForeignKey(e => e.RevisionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SeoContentReviewEvent>()
            .HasIndex(e => new { e.PageId, e.OccurredAt });

        modelBuilder.Entity<Guest>().HasIndex(g => g.Email);

        // PricingAdapterConfig → Property (1-to-1)
        modelBuilder.Entity<PricingAdapterConfig>()
            .HasOne(c => c.Property)
            .WithOne(p => p.PricingAdapterConfig)
            .HasForeignKey<PricingAdapterConfig>(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PricingAdapterConfig>()
            .HasIndex(c => new { c.PropertyId, c.IsEnabled });

        // PricingHistory → Property
        modelBuilder.Entity<PricingHistory>()
            .HasOne(h => h.Property)
            .WithMany()
            .HasForeignKey(h => h.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PricingHistory>()
            .HasIndex(h => new { h.PropertyId, h.AdaptationDate });

        // PC-15: one seasonal suggestion per property and stay date, regenerated in place (upsert by date).
        modelBuilder.ApplyConfiguration(new SeasonalPriceSuggestionConfiguration());

        // PropertyDocument → Property
        modelBuilder.Entity<PropertyDocument>()
            .HasOne(d => d.Property)
            .WithMany(p => p.PropertyDocuments)
            .HasForeignKey(d => d.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PropertyDocument>()
            .HasIndex(d => d.PropertyId)
            .HasDatabaseName("IX_PropertyDocuments_PropertyId");

        modelBuilder.Entity<PropertyDocument>()
            .Property(d => d.DocumentType)
            .HasConversion<string>()
            .HasMaxLength(100);

        // Demo environment: one platform row, and one marker per invented row so a reset never touches host data.
        modelBuilder.ApplyConfiguration(new DemoEnvironmentSettingConfiguration());
        modelBuilder.ApplyConfiguration(new TestDataMarkerConfiguration());

        modelBuilder.Entity<LeaseDocument>().HasOne(d => d.LeaseContract).WithMany()
            .HasForeignKey(d => d.LeaseContractId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaseDocument>().HasIndex(d => new { d.LeaseContractId, d.UploadedAt });
        modelBuilder.Entity<LeaseDocument>().HasIndex(d => d.OrgId);

        // LeaseContract → Property (restrict to preserve history)
        modelBuilder.Entity<LeaseContract>()
            .HasOne(l => l.Property)
            .WithMany()
            .HasForeignKey(l => l.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<LeaseContract>()
            .Property(l => l.MonthlyRent)
            .HasPrecision(18, 2);

        // Canone concordato characteristics and range of the lease (LT-10): same table, optional.
        modelBuilder.Entity<LeaseContract>().OwnsOne(l => l.ConcordatoAssessment);
        modelBuilder.Entity<LeaseContract>().Navigation(l => l.ConcordatoAssessment).IsRequired(false);

        modelBuilder.Entity<LeaseContract>().HasIndex(l => l.PropertyId);
        modelBuilder.Entity<LeaseContract>().HasIndex(l => l.Status);

        // LR-14: what is asked of the leases of an org is asked of the end of the period they are in now (the lists, the deadlines, the
        // occupancy of a property, the tacit renewal job), never of the end the contract states.
        modelBuilder.Entity<LeaseContract>().HasIndex(l => new { l.OrgId, l.CurrentTermEndDate });

        // Party → LeaseContract (cascade)
        modelBuilder.Entity<Party>()
            .HasOne(p => p.LeaseContract)
            .WithMany(l => l.Parties)
            .HasForeignKey(p => p.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        // LT-14: several parties per role, in the order entered.
        modelBuilder.Entity<Party>()
            .HasIndex(p => new { p.LeaseContractId, p.Role, p.Position })
            .IsUnique();

        // LeaseRegistration → LeaseContract (1-to-1, cascade)
        modelBuilder.Entity<LeaseRegistration>()
            .HasOne(r => r.LeaseContract)
            .WithOne(l => l.Registration)
            .HasForeignKey<LeaseRegistration>(r => r.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LeaseRegistration>()
            .HasIndex(r => r.LeaseContractId)
            .IsUnique();

        // LT-01 (A7-01): "Registered" only with the official receipt stored, never as a simulation.
        modelBuilder.Entity<LeaseRegistration>()
            .ToTable(t => t.HasCheckConstraint(
                "CK_LeaseRegistrations_RegisteredRequiresReceipt",
                $"\"Status\" <> {(int)RegistrationStatus.Registered} OR btrim(coalesce(\"ReceiptStoragePath\", '')) <> ''"));

        // LeaseEvent → LeaseContract (cascade)
        modelBuilder.Entity<LeaseEvent>()
            .HasOne(e => e.LeaseContract)
            .WithMany(l => l.Events)
            .HasForeignKey(e => e.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LeaseEvent>()
            .HasIndex(e => new { e.LeaseContractId, e.OccurredAt });

        // LeaseSigner (LT-02, A7-16): one row per party and lease, removed with the lease or the party.
        modelBuilder.Entity<LeaseSigner>()
            .HasOne(s => s.LeaseContract)
            .WithMany(l => l.Signers)
            .HasForeignKey(s => s.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaseSigner>()
            .HasOne(s => s.Party)
            .WithMany()
            .HasForeignKey(s => s.PartyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaseSigner>()
            .HasOne(s => s.Org)
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LeaseSigner>()
            .HasIndex(s => new { s.LeaseContractId, s.PartyId })
            .IsUnique();

        modelBuilder.Entity<LeaseRegistrationAuthorization>()
            .HasOne(a => a.LeaseContract)
            .WithMany()
            .HasForeignKey(a => a.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<LeaseRegistrationAuthorization>()
            .HasOne(a => a.Org)
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LeaseRegistrationAuthorization>()
            .HasIndex(a => a.LeaseContractId);
        modelBuilder.Entity<LeaseRegistrationAuthorization>().HasIndex(a => a.OrgId);

        modelBuilder.Entity<RentSchedule>()
            .HasOne(s => s.LeaseContract)
            .WithOne(l => l.RentSchedule)
            .HasForeignKey<RentSchedule>(s => s.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentSchedule>()
            .HasOne(s => s.Org)
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentSchedule>()
            .HasIndex(s => s.LeaseContractId)
            .IsUnique();

        modelBuilder.Entity<RentSchedule>()
            .Property(s => s.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<RentSchedule>()
            .HasIndex(s => s.OrgId);

        modelBuilder.Entity<RentLedgerEntry>()
            .HasOne(e => e.LeaseContract)
            .WithMany()
            .HasForeignKey(e => e.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentLedgerEntry>()
            .HasOne(e => e.RentSchedule)
            .WithMany(s => s.LedgerEntries)
            .HasForeignKey(e => e.RentScheduleId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentLedgerEntry>()
            .HasOne(e => e.Org)
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentLedgerEntry>()
            .HasIndex(e => new { e.LeaseContractId, e.PeriodStart })
            .IsUnique();

        modelBuilder.Entity<RentLedgerEntry>()
            .Property(e => e.AmountDue)
            .HasPrecision(18, 2);

        modelBuilder.Entity<RentLedgerEntry>()
            .Property(e => e.StampDutyAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<RentLedgerEntry>()
            .HasIndex(e => e.OrgId);

        // The rent register of the long-term area (LR-01): the installments of an org by month of due date, in due date order.
        modelBuilder.Entity<RentLedgerEntry>()
            .HasIndex(e => new { e.OrgId, e.DueDate });

        // The credits of a bank statement confirmed as rent payments (import of the statement of the Canoni page): the fingerprint is
        // unique per org, so the same movement is imported once; the payer hashes are looked up per org to recognize who paid before.
        modelBuilder.Entity<RentBankMovement>()
            .HasOne(m => m.LeaseContract)
            .WithMany()
            .HasForeignKey(m => m.LeaseContractId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentBankMovement>()
            .HasOne(m => m.RentLedgerEntry)
            .WithMany()
            .HasForeignKey(m => m.RentLedgerEntryId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentBankMovement>()
            .HasOne(m => m.Org)
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<RentBankMovement>()
            .Property(m => m.Amount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<RentBankMovement>()
            .HasIndex(m => new { m.OrgId, m.Fingerprint })
            .IsUnique();

        modelBuilder.Entity<RentBankMovement>().HasIndex(m => new { m.OrgId, m.PayerIbanHash });
        modelBuilder.Entity<RentBankMovement>().HasIndex(m => new { m.OrgId, m.PayerNameHash });
        modelBuilder.Entity<RentBankMovement>().HasIndex(m => m.LeaseContractId);
        modelBuilder.Entity<RentBankMovement>().HasIndex(m => m.RentLedgerEntryId);

        // ConcordatoFacile integration (NF-02, NF-03, ADR-004)
        modelBuilder.ApplyConfiguration(new ConcordatoFacilePurchaseConfiguration());
        modelBuilder.ApplyConfiguration(new ConcordatoFacileGiftConfiguration());
        modelBuilder.ApplyConfiguration(new CfSimulatedPracticeConfiguration());

        // ─── Multi-tenant Org boundary (US-004) ──────────────────────────────────
        // Org tenant key with a unique Slug (AC1).
        modelBuilder.Entity<Org>()
            .HasIndex(o => o.Slug)
            .IsUnique();

        modelBuilder.Entity<Org>()
            .HasIndex(o => o.StripeCustomerId);

        // The language of the account (AM-08b-b): the orgs that exist when the column is added get Italian, the language they have always had.
        modelBuilder.Entity<Org>()
            .Property(o => o.DefaultLanguage)
            .HasDefaultValue(Casazen.Core.OrgTeam.OrgInvitationRules.DefaultLanguage);

        // Previous public slugs of an org (PL-04, A1-23): shared links keep resolving and the value stays reserved.
        modelBuilder.Entity<OrgSlugAlias>()
            .HasOne(a => a.Org)
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrgSlugAlias>()
            .HasIndex(a => a.OrgId);

        // Operator documents of the public site (BK-14, A3-21): immutable versions numbered per org and kind. The unique
        // index is the guarantee of the numbering under concurrent publishes (23505, the loser takes the next number).
        modelBuilder.ApplyConfiguration(new OrgSiteDocumentConfiguration());

        modelBuilder.Entity<Org>()
            .HasIndex(o => o.CustomDomain)
            .IsUnique()
            .HasFilter("\"CustomDomain\" IS NOT NULL AND \"DomainVerificationStatus\" = 1");

        modelBuilder.Entity<Org>()
            .HasIndex(o => o.Subdomain)
            .IsUnique()
            .HasFilter("\"Subdomain\" IS NOT NULL");

        modelBuilder.Entity<PlatformInvoice>()
            .HasOne(i => i.Org)
            .WithMany()
            .HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PlatformInvoice>()
            .HasIndex(i => i.StripeInvoiceId)
            .IsUnique();

        modelBuilder.Entity<PlatformInvoice>()
            .HasIndex(i => i.OrgId);

        modelBuilder.Entity<PlatformInvoice>()
            .HasIndex(i => i.SdiStatus);

        modelBuilder.Entity<PlatformInvoice>()
            .Property(i => i.AmountExVat)
            .HasPrecision(18, 2);

        modelBuilder.Entity<PlatformInvoice>()
            .Property(i => i.VatAmount)
            .HasPrecision(18, 2);

        modelBuilder.Entity<PlatformInvoice>()
            .Property(i => i.TotalAmount)
            .HasPrecision(18, 2);

        // OrgId indexes on the tenant-scoped tables + Users (AC2/AC9).
        modelBuilder.Entity<Property>().HasIndex(p => p.OrgId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.OrgId);
        modelBuilder.Entity<LeaseContract>().HasIndex(l => l.OrgId);
        modelBuilder.Entity<Payment>().HasIndex(p => p.OrgId);
        modelBuilder.Entity<User>().HasIndex(u => u.OrgId);
        modelBuilder.Entity<Guest>().HasIndex(g => g.OrgId);
        // TN-2: child rows that controllers expose carry their parent's OrgId.
        modelBuilder.Entity<PropertyDocument>().HasIndex(d => d.OrgId);
        modelBuilder.Entity<OtaIntegration>().HasIndex(o => o.OrgId);
        modelBuilder.Entity<PricingAdapterConfig>().HasIndex(c => c.OrgId);
        modelBuilder.Entity<PricingHistory>().HasIndex(h => h.OrgId);
        modelBuilder.Entity<AlloggiatiWebReport>().HasIndex(r => r.OrgId);

        // OrgId FK constraints (AC2). Restrict: an Org can never be deleted while it still owns
        // tenant rows. The four tenant tables are required (Guid); User.OrgId is nullable (AC9).
        modelBuilder.Entity<Property>()
            .HasOne(p => p.Org).WithMany().HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Booking>()
            .HasOne(b => b.Org).WithMany().HasForeignKey(b => b.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<LeaseContract>()
            .HasOne(l => l.Org).WithMany().HasForeignKey(l => l.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Payment>()
            .HasOne(p => p.Org).WithMany().HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PropertyFiscalYear>()
            .HasOne(y => y.Org).WithMany().HasForeignKey(y => y.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PropertyFiscalYear>()
            .HasOne(y => y.Property).WithMany().HasForeignKey(y => y.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PropertyFiscalYear>()
            .HasIndex(y => new { y.PropertyId, y.TaxYear })
            .IsUnique();
        // No unique "one primary per org and year" index any more: the 21% unit is one per taxpayer (CO-18), and one org
        // can manage several taxpayers. FiscalService checks it under the OrgFiscalRegime advisory lock.
        modelBuilder.Entity<PropertyFiscalYear>()
            .HasIndex(y => new { y.OrgId, y.TaxYear });
        modelBuilder.Entity<PropertyFiscalYear>().HasIndex(y => y.OrgId);
        modelBuilder.Entity<Payment>()
            .Property(p => p.OtaWithholdingTax)
            .HasPrecision(18, 2);
        modelBuilder.Entity<Payment>()
            .Property(p => p.NetAmountAfterWithholding)
            .HasPrecision(18, 2);
        modelBuilder.Entity<User>()
            .HasOne(u => u.Org).WithMany().HasForeignKey(u => u.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        // TN-1: a guest belongs to exactly one org. No unique (OrgId, lower(Email)) index: host and
        // direct bookings store one guest snapshot per booking (#431), so one org legitimately holds
        // several rows with the same e-mail.
        modelBuilder.Entity<Guest>()
            .HasOne(g => g.Org).WithMany().HasForeignKey(g => g.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        // TN-2: child rows copy the OrgId of their parent (property or booking) when they are created.
        // Restrict like every other OrgId FK; no navigation, the org is never loaded through a child.
        modelBuilder.Entity<PropertyDocument>()
            .HasOne<Org>().WithMany().HasForeignKey(d => d.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<OtaIntegration>()
            .HasOne<Org>().WithMany().HasForeignKey(o => o.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PricingAdapterConfig>()
            .HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PricingHistory>()
            .HasOne<Org>().WithMany().HasForeignKey(h => h.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AlloggiatiWebReport>()
            .HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<GuestCheckInSession>()
            .HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<GuestCheckInSession>().HasIndex(s => s.OrgId);

        modelBuilder.Entity<AppContextEntity>()
            .HasKey(c => c.Key);

        modelBuilder.Entity<Role>()
            .HasIndex(r => new { r.ContextKey, r.RoleKey })
            .IsUnique();

        modelBuilder.Entity<Role>()
            .HasOne(r => r.Context)
            .WithMany(c => c.Roles)
            .HasForeignKey(r => r.ContextKey)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RolePermission>()
            .HasKey(rp => new { rp.RoleId, rp.PermissionKey });

        modelBuilder.Entity<RolePermission>()
            .HasOne(rp => rp.Role)
            .WithMany(r => r.Permissions)
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserContextMembership>()
            .HasIndex(m => new { m.UserId, m.ContextKey })
            .IsUnique();

        modelBuilder.Entity<UserContextMembership>()
            .HasOne(m => m.User)
            .WithMany(u => u.ContextMemberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserContextMembership>()
            .HasOne(m => m.Context)
            .WithMany(c => c.Memberships)
            .HasForeignKey(m => m.ContextKey)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserContextMembership>()
            .HasOne(m => m.Role)
            .WithMany(r => r.Memberships)
            .HasForeignKey(m => m.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

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

        // The member in charge of a property (AM-03): notified together with the org's administrators. The account can go;
        // the property keeps its history and is simply left without a named person.
        modelBuilder.Entity<Property>()
            .HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.ResponsibleUserId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Property>()
            .HasIndex(p => p.ResponsibleUserId)
            .HasDatabaseName("IX_Properties_ResponsibleUserId");

        modelBuilder.Entity<ConsentRecord>()
            .HasIndex(c => new { c.UserId, c.OrgId, c.Type });

        // ─── Signup attribution (SE-03 / A8-03) ─────────────────────────────────
        modelBuilder.ApplyConfiguration(new SignupAttributionConfiguration());

        // SEO funnel events (SE-04): platform data without an org or a person; the report groups by comune over a window,
        // the nightly retention deletes by date.
        modelBuilder.ApplyConfiguration(new SeoEventConfiguration());

        // ─── Supplier console (US-022 / #292) ────────────────────────────────────
        modelBuilder.Entity<SupplierProfile>()
            .HasOne(sp => sp.Org)
            .WithOne()
            .HasForeignKey<SupplierProfile>(sp => sp.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierProfile>()
            .HasIndex(sp => sp.Status);

        // SU-04: comuni chosen from the official list. A column added to a table with rows: the profiles that exist start
        // with none (nothing is inferred from the free-text ComuniJson).
        modelBuilder.Entity<SupplierProfile>()
            .Property(sp => sp.ComuneIstatCodesJson)
            .HasDefaultValueSql("'[]'::jsonb");

        modelBuilder.Entity<SupplierProfile>()
            .HasIndex(sp => sp.ClaimTokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_SupplierProfiles_ClaimTokenHash");

        // SU-13: the slug of the public showcase is unique (profiles without one are not constrained).
        modelBuilder.Entity<SupplierProfile>()
            .HasIndex(sp => sp.ShowcaseSlug)
            .IsUnique()
            .HasFilter("\"ShowcaseSlug\" IS NOT NULL")
            .HasDatabaseName("UIX_SupplierProfiles_ShowcaseSlug");

        // One profile per email (SU-14): the unique index on lower(btrim("Email")) is an expression index that EF cannot
        // model; it is created by the migration SupplierProfileEmailUnique (see SupplierProfileEmailIndex).

        modelBuilder.Entity<SupplierAvailability>()
            .HasOne(sa => sa.SupplierProfile)
            .WithMany()
            .HasForeignKey(sa => sa.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupplierAvailability>()
            .HasIndex(sa => new { sa.OrgId, sa.Date })
            .IsUnique();

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

        modelBuilder.Entity<SupplierInviteRecord>()
            .HasIndex(i => i.Email);

        modelBuilder.Entity<SupplierInviteRecord>()
            .HasIndex(i => new { i.Email, i.IsUsed });

        modelBuilder.Entity<SupplierInviteRecord>()
            .HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_SupplierInviteRecords_TokenHash");

        // Audit trail of the admin actions on suppliers and invites (SU-12): read per supplier, newest first. No foreign
        // key: the trail outlives a supplier org deleted by the fix-orphaned repair.
        modelBuilder.Entity<SupplierAdminAuditEntry>()
            .HasIndex(e => new { e.SupplierOrgId, e.OccurredAt });

        // ─── Host invites (invitation of new independent hosts) ──────────────────
        modelBuilder.ApplyConfiguration(new HostInviteRecordConfiguration());

        // Audit trail and source of the daily quota: read by host org and time window. No foreign key: it outlives a purged invite.
        modelBuilder.ApplyConfiguration(new HostInviteAuditEntryConfiguration());

        // ─── Micro-marketplace v0 (US-021 / #293) ────────────────────────────────
        modelBuilder.Entity<ServiceRequest>()
            .HasOne(sr => sr.Org)
            .WithMany()
            .HasForeignKey(sr => sr.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ServiceRequest>()
            .HasOne(sr => sr.Property)
            .WithMany()
            .HasForeignKey(sr => sr.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ServiceRequest>()
            .HasOne(sr => sr.SupplierOrg)
            .WithMany()
            .HasForeignKey(sr => sr.SupplierOrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ServiceRequest>()
            .HasOne(sr => sr.Booking)
            .WithMany()
            .HasForeignKey(sr => sr.BookingId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ServiceRequest>()
            .HasIndex(sr => new { sr.OrgId, sr.Status });

        modelBuilder.Entity<ServiceRequest>()
            .HasIndex(sr => new { sr.SupplierOrgId, sr.Status });

        // A4-19 (SU-10): Npgsql maps a uint row version to the xmin system column, so every state transition is saved
        // only if the row was not changed since it was read.
        modelBuilder.Entity<ServiceRequest>()
            .Property(sr => sr.Version)
            .IsRowVersion();

        // SP-04: a request has an optional catalog service (set to null if the service is ever removed for good; the catalog
        // deletes softly and the request keeps its own copy of the name), and the planner and the console read a supplier's
        // requests by the time of the work.
        modelBuilder.Entity<ServiceRequest>()
            .HasOne<SupplierServiceListing>()
            .WithMany()
            .HasForeignKey(sr => sr.ServiceListingId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ServiceRequest>()
            .HasIndex(sr => new { sr.SupplierOrgId, sr.ScheduledStartUtc });

        // The JSON columns are added to a table that already has rows: the database gives those the empty list (an empty string
        // is not valid jsonb), the entity gives it to the new ones.
        modelBuilder.ApplyConfiguration(new ServiceRequestPricingConfiguration());

        // The checks mirror ServiceRequestLimits and the rules of the service: a bad row is refused by the database too. A
        // time has both ends or none, a proposal has its start, end and instant or none, an amount is from 1 cent to the bound.
        modelBuilder.Entity<ServiceRequest>().ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServiceRequests_ScheduledInterval",
                "(\"ScheduledStartUtc\" IS NULL AND \"ScheduledEndUtc\" IS NULL) OR "
                + "(\"ScheduledStartUtc\" IS NOT NULL AND \"ScheduledEndUtc\" IS NOT NULL AND \"ScheduledEndUtc\" > \"ScheduledStartUtc\")");
            t.HasCheckConstraint(
                "CK_ServiceRequests_ProposedInterval",
                "(\"ProposedStartUtc\" IS NULL AND \"ProposedEndUtc\" IS NULL AND \"ProposedAt\" IS NULL) OR "
                + "(\"ProposedStartUtc\" IS NOT NULL AND \"ProposedEndUtc\" IS NOT NULL AND \"ProposedAt\" IS NOT NULL "
                + "AND \"ProposedEndUtc\" > \"ProposedStartUtc\")");
            t.HasCheckConstraint(
                "CK_ServiceRequests_Amounts",
                $"(\"EstimatedAmountCents\" IS NULL OR \"EstimatedAmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents}) AND "
                + $"(\"QuotedAmountCents\" IS NULL OR \"QuotedAmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents}) AND "
                + $"(\"FinalAmountCents\" IS NULL OR \"FinalAmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents})");
        });

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

        // Whether an org's requests also go to backup suppliers: on for every org that exists (the rule is general), so the migration gives
        // the existing rows true. EF itself always writes the value of the entity: left to generate it, EF would take an explicit false for
        // «not set» and the insert would write the default (true) in its place, turning the rule back on for an org that switched it off.
        modelBuilder.Entity<Org>()
            .Property(o => o.SupplierBackupsEnabled)
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // ─── Quote with the customer's approval (SP-04b) ──────────────────────────────────────────────────────────────
        // The offer a supplier makes on a request still waiting for its answer: its columns, and the checks that keep it true.
        modelBuilder.ApplyConfiguration(new ServiceRequestQuoteConfiguration());

        modelBuilder.ApplyConfiguration(new ServiceCustomerConfiguration());

        modelBuilder.ApplyConfiguration(new ShowcaseBookingHoldConfiguration());

        // The request on measure (SP-10b): the inquiry that waits for the e-mail check and the messages of its thread.
        modelBuilder.ApplyConfiguration(new ShowcaseInquiryConfiguration());
        modelBuilder.ApplyConfiguration(new ServiceRequestMessageConfiguration());

        // ─── Payment of the service requests inside CasaZen (SP-15a) ─────────────
        // The commission a supplier is charged instead of the platform's (an admin sets it, SP-15b): a percentage, 0 to 50.
        modelBuilder.Entity<SupplierProfile>().ToTable(t =>
            t.HasCheckConstraint(
                "CK_SupplierProfiles_CommissionPercentOverride",
                $"\"CommissionPercentOverride\" IS NULL OR \"CommissionPercentOverride\" BETWEEN 0 AND {(int)SupplierPaymentsOptions.MaxCommissionPercent}"));

        // One payment per request that is not canceled: the live one. A canceled row (replaced by an offline record, or withdrawn)
        // frees the request, so the index ignores it. The creation paths also take the advisory lock ServiceRequestPayment; this
        // index is what makes a second live payment impossible even if one of them did not.
        modelBuilder.ApplyConfiguration(new ServiceRequestPaymentConfiguration());

        // ─── Refunds of the service payments and the commission period (SP-15b) ─────────────
        // The end of a commission period only makes sense with the percentage it ends.
        modelBuilder.Entity<SupplierProfile>().ToTable(t =>
            t.HasCheckConstraint(
                "CK_SupplierProfiles_CommissionOverrideUntil",
                "\"CommissionOverrideUntil\" IS NULL OR \"CommissionPercentOverride\" IS NOT NULL"));

        // A refund belongs to its payment: restrict, so a payment with refunds is never deleted. The sequence is the n of the
        // idempotency key and is unique within the payment; a Stripe refund and a key belong to one refund each.
        modelBuilder.ApplyConfiguration(new ServiceRequestPaymentRefundConfiguration());

        // ─── The complaint and the dispute about a job paid through CasaZen (SP-15c-c) ─────────────
        // One open dispute per payment (unique partial index); a payment, a request and a supplier with a dispute are never deleted.
        modelBuilder.ApplyConfiguration(new ServicePaymentDisputeConfiguration());

        // ─── Property iCal OTA sync (US-018 / #294) ─────────────────────────────
        modelBuilder.Entity<CalendarBlock>()
            .HasOne(b => b.Property)
            .WithMany()
            .HasForeignKey(b => b.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CalendarBlock>()
            .HasOne(b => b.Org)
            .WithMany()
            .HasForeignKey(b => b.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // Blocks belong to their import feed (PC-11, A2-11): a UID is unique within its feed, and removing the feed
        // removes its blocks. Airbnb and Booking.com feeds of the same property never touch each other's blocks.
        modelBuilder.Entity<CalendarBlock>()
            .HasOne(b => b.Feed)
            .WithMany()
            .HasForeignKey(b => b.FeedId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CalendarBlock>()
            .HasIndex(b => new { b.FeedId, b.ExternalUid })
            .IsUnique();

        // The index of the foreign key to the property, declared here so that it stays: EF drops the index of a foreign key when another index
        // starts with the same column, filtered or not, and the filtered index below holds the Octorate blocks only. Every read of the blocks
        // of a property (availability, calendar, export) goes through this one.
        modelBuilder.Entity<CalendarBlock>()
            .HasIndex(b => b.PropertyId);

        // The blocks read from the Octorate calendar have no feed (E02/F02/US01/T03): the unique index on (FeedId, ExternalUid) does not cover
        // them (a null feed is never equal to another), so their key is the property and the UID, for that source only
        // (CalendarBlockSource.Octorate = 2).
        modelBuilder.Entity<CalendarBlock>()
            .HasIndex(b => new { b.PropertyId, b.ExternalUid })
            .IsUnique()
            .HasFilter("\"Source\" = 2")
            .HasDatabaseName(CalendarBlock.OctorateUidIndexName);

        // OTA stay created from an imported block (CO-21, D7): one stay per block. Deleting the booking keeps the block,
        // which takes its nights again on its own.
        modelBuilder.Entity<CalendarBlock>()
            .HasOne(b => b.Booking)
            .WithMany()
            .HasForeignKey(b => b.BookingId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CalendarBlock>()
            .HasIndex(b => b.BookingId)
            .IsUnique();

        // The stays of a feed, found again by the sync (feed + block UID) when a block comes back.
        modelBuilder.Entity<Booking>()
            .HasIndex(b => new { b.ICalFeedId, b.ExternalId });

        modelBuilder.Entity<PropertyICalFeed>()
            .HasOne(f => f.Property)
            .WithMany()
            .HasForeignKey(f => f.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PropertyICalFeed>()
            .HasOne(f => f.Org)
            .WithMany()
            .HasForeignKey(f => f.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PropertyICalFeed>()
            .HasIndex(f => f.PropertyId);

        modelBuilder.Entity<PropertyICalExport>()
            .HasOne(e => e.Property)
            .WithMany()
            .HasForeignKey(e => e.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PropertyICalExport>()
            .HasOne(e => e.Org)
            .WithMany()
            .HasForeignKey(e => e.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<PropertyICalExport>()
            .HasIndex(e => e.PropertyId)
            .IsUnique();

        // ─── Scheduled change of rental mode (PM-02) ────────────────────────────
        modelBuilder.ApplyConfiguration(new PropertyModeChangeConfiguration());

        modelBuilder.Entity<PropertyICalExport>()
            .HasIndex(e => e.ExportToken)
            .IsUnique();

        modelBuilder.Entity<AppContextEntity>().HasData(
            new AppContextEntity { Key = "short-rent", DisplayName = "Affitti brevi" },
            new AppContextEntity { Key = "long-rent", DisplayName = "Affitti lungo termine" },
            new AppContextEntity { Key = "admin", DisplayName = "Amministrazione" });

        modelBuilder.Entity<Role>().HasData(
            new Role { Id = 1, ContextKey = "short-rent", RoleKey = "property_owner" },
            new Role { Id = 2, ContextKey = "long-rent", RoleKey = "long_term_landlord" },
            new Role { Id = 3, ContextKey = "admin", RoleKey = "platform_admin" });

        modelBuilder.Entity<RolePermission>().HasData(
            new RolePermission { RoleId = 1, PermissionKey = "property.read" },
            new RolePermission { RoleId = 1, PermissionKey = "property.write" },
            new RolePermission { RoleId = 1, PermissionKey = "booking.read" },
            new RolePermission { RoleId = 1, PermissionKey = "booking.write" },
            new RolePermission { RoleId = 1, PermissionKey = "payment.read" },
            new RolePermission { RoleId = 1, PermissionKey = "payment.write" },
            new RolePermission { RoleId = 1, PermissionKey = "ota.read" },
            new RolePermission { RoleId = 1, PermissionKey = "ota.write" },
            new RolePermission { RoleId = 1, PermissionKey = "guest.read" },
            new RolePermission { RoleId = 1, PermissionKey = "guest.write" },
            // AM-03: carved out of property.write, guest.write and booking.write; the owner keeps doing all of it.
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.ServiceRequestWrite },
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.GuestManage },
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.AlloggiatiSubmit },
            // E03/F04/US02/T01: the owner decides the identity verifications of its guests (AddIdentityVerificationFields).
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.VerificationDecision },
            new RolePermission { RoleId = 2, PermissionKey = "property.read" },
            new RolePermission { RoleId = 2, PermissionKey = "property.write" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.read" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.create" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.sign" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.register" },
            new RolePermission { RoleId = 2, PermissionKey = "rent.read" },
            new RolePermission { RoleId = 2, PermissionKey = "rent.manage" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.stats.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.users.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.users.manage" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.cin.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.jobs.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.tax.manage" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.seo.read" });

        // AM-01: the account context and the roles of the org team. OrgRoleCatalog is the source (ids 4 to 12); the tests
        // compare this seed with it and with OrgOwnerRoles.
        modelBuilder.Entity<AppContextEntity>().HasData(
            new AppContextEntity { Key = AccountContext.Key, DisplayName = AccountContext.DisplayName });

        modelBuilder.Entity<Role>().HasData(
            OrgRoleCatalog.SeededRoles.Select(r => new Role { Id = r.Id, ContextKey = r.ContextKey, RoleKey = r.RoleKey }));

        modelBuilder.Entity<RolePermission>().HasData(
            OrgRoleCatalog.SeededRoles.SelectMany(r =>
                r.Permissions.Select(p => new RolePermission { RoleId = r.Id, PermissionKey = p })));

        // Guest check-in session (US-020 / #296)
        modelBuilder.ApplyConfiguration(new GuestCheckInSessionConfiguration());

        // E03/F04/US02/T01: who decided a verification and why. The rows written before the columns are the agent's (Biometric = 1) with no
        // reasons; the sentinel makes EF always write the value of the entity, so a new row is never left to the default by mistake.
        modelBuilder.Entity<BiometricVerificationLog>()
            .Property(l => l.Method)
            .HasDefaultValue(VerificationMethod.Biometric)
            .HasSentinel((VerificationMethod)(-1));
        modelBuilder.Entity<BiometricVerificationLog>()
            .Property(l => l.ReasonsJson)
            .HasDefaultValueSql("'[]'::jsonb");

        modelBuilder.ApplyConfiguration(new TerritorialRentAgreementConfiguration());

        modelBuilder.Entity<HighTensionAreaComune>()
            .HasIndex(c => c.Comune);

        modelBuilder.Entity<ComuneImuChannel>()
            .HasIndex(c => c.Comune);

        modelBuilder.Entity<RegulatoryDataAuditEntry>()
            .HasIndex(e => new { e.EntityId, e.OccurredAt });

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

        // ─── The OTA conversations, copied from the channel manager (E05/F01/US01/T01, ADR-032) ───────────────
        // One thread per Octorate thread and organization (UIX_OtaMessageThreads_OrgId_OctorateThreadId), one message per Octorate message
        // (UIX_OtaMessages_OrgId_OctorateMessageId) and per key of the sender's screen in a thread; the words are encrypted (EncryptedColumns).
        // The checkpoints of the sync are one row per organization and accommodation; the templates are unique per organization, name and language.
        modelBuilder.ApplyConfiguration(new OtaMessageThreadConfiguration());
        modelBuilder.ApplyConfiguration(new OtaMessageConfiguration());
        modelBuilder.ApplyConfiguration(new OtaMessageSyncStateConfiguration());
        modelBuilder.ApplyConfiguration(new MessageTemplateConfiguration());

        // ─── Automation routines (NF-26, ADR-022) ────────────────────────────────
        // Unique constraint on (RoutineId, EventId) ensures idempotence: a routine executes at most once per event (ADR-022-R7).
        modelBuilder.Entity<RoutineExecution>()
            .HasIndex(e => new { e.RoutineId, e.EventId })
            .IsUnique();

        // ─── Transactional outbox of the domain events (ADR-022) ─────────────────
        // A time trigger emitted twice (a restart, two overlapping runs) loses on the unique key of its org (ADR-022-R8); the events
        // without a key are never compared. The dispatcher reads the undispatched events by OccurredAt and the cleanup the old
        // dispatched ones: both through (DispatchedAt, OccurredAt).
        modelBuilder.Entity<OutboxEvent>(entity =>
        {
            entity.HasIndex(e => new { e.OrgId, e.DedupKey })
                .IsUnique()
                .HasFilter("\"DedupKey\" IS NOT NULL")
                .HasDatabaseName(OutboxEvent.DedupIndexName);
            entity.HasIndex(e => new { e.DispatchedAt, e.OccurredAt });
        });

        // ─── Octorate connector (ADR-023) ────────────────────────────────────────
        // One connection per org and environment: one consent covers every property the Octorate user sees (an agency's whole
        // network), and the client acts for the org of its scope. The account id is for display and audit, not part of the key. The
        // two token columns get the Data Protection converter from EncryptedColumns (no length: the payload is longer than the text).
        modelBuilder.Entity<OctorateConnection>(entity =>
        {
            entity.HasIndex(c => new { c.OrgId, c.Environment })
                .IsUnique()
                .HasDatabaseName(OctorateConnection.OrgEnvironmentIndexName);
        });

        // A listing found through a connection, once per org, connection and accommodation; a property is confirmed to one listing
        // only (partial unique index on Status = 2, OctorateMappingStatus.Confirmed). A connection with mappings is never deleted
        // (RESTRICT); a deleted property leaves its mapping without property (SET NULL). The webhook finds the org of a notification
        // by the accommodation code across orgs (a system read): its own index.
        modelBuilder.Entity<OctorateListingMapping>(entity =>
        {
            entity.HasOne<OctorateConnection>()
                .WithMany()
                .HasForeignKey(m => m.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Property>()
                .WithMany()
                .HasForeignKey(m => m.PropertyId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            entity.Property(m => m.SnapshotRoomsJson).HasColumnType("jsonb");
            entity.Property(m => m.CandidatePropertyIdsJson).HasColumnType("jsonb");
            entity.Property(m => m.ChannelIdsJson).HasColumnType("jsonb");
            entity.HasIndex(m => new { m.OrgId, m.ConnectionId, m.OctorateAccommodationId })
                .IsUnique()
                .HasDatabaseName(OctorateListingMapping.AccommodationIndexName);
            entity.HasIndex(m => new { m.OrgId, m.PropertyId })
                .IsUnique()
                .HasFilter("\"Status\" = 2")
                .HasDatabaseName(OctorateListingMapping.ConfirmedPropertyIndexName);
            entity.HasIndex(m => new { m.OrgId, m.Status });
            entity.HasIndex(m => m.OctorateAccommodationId);
        });

        // The import of existing OTA inventory (D6, E02/F01/US03): one current intent for each property (partial unique indexes), batches with their
        // channels and the external items done for each connection; every row carries its org (restrict) and the tenant filter applies to all.
        modelBuilder.ApplyConfiguration(new OctorateInventoryIntentConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateInventoryImportBatchConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateInventoryImportChannelConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateInventoryImportItemConfiguration());

        // The OAuth state of a consent: only its SHA-256 is stored (char 64, unique); the expired rows are deleted by ExpiresAt.
        modelBuilder.Entity<OctorateOAuthState>(entity =>
        {
            entity.Property(s => s.StateHash).IsFixedLength();
            entity.HasIndex(s => s.StateHash)
                .IsUnique()
                .HasDatabaseName(OctorateOAuthState.StateHashIndexName);
            entity.HasIndex(s => s.ExpiresAt);
        });

        // The mirror of a CasaZen booking on Octorate (E08/F01/US01/T01, ADR-031): one row per booking, gone with its booking (CASCADE);
        // the list of the failures of an org reads (OrgId, State). The dates Octorate holds after a 409 are jsonb.
        modelBuilder.Entity<OctorateBookingSync>(entity =>
        {
            entity.HasOne<Booking>()
                .WithMany()
                .HasForeignKey(s => s.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(s => s.OctorateVersionJson).HasColumnType("jsonb");
            entity.HasIndex(s => s.BookingId)
                .IsUnique()
                .HasDatabaseName(OctorateBookingSync.BookingIndexName);
            entity.HasIndex(s => new { s.OrgId, s.State });
        });

        // ─── Invoice data and issuer of the documents (epic E06, NF-81, NF-82) ───────────────────────────────
        // The invoice data of a booking: one row for each booking (unique BookingId), gone with the booking (cascade); the personal columns are
        // encrypted by EncryptedColumns (no length) and xmin is the concurrency token. The tenant query filter is applied below, as to every
        // ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new BookingInvoiceDataConfiguration());

        // The profile of the issuer of an org (unique OrgId) and the single issuer of a booking (unique BookingId, cascade with the booking):
        // the database itself refuses a second one of either (FIS-02, NF-82).
        modelBuilder.ApplyConfiguration(new OrgFiscalIssuerProfileConfiguration());
        modelBuilder.ApplyConfiguration(new BookingDocumentIssuerConfiguration());

        // A payment is recorded on its Octorate reservation once (E08/F01/US01/T01, AC-NF74-4): its transaction is unique in the org. Only
        // the payments recorded there are in the index.
        modelBuilder.Entity<Payment>()
            .HasIndex(p => new { p.OrgId, p.OctorateTransactionId })
            .IsUnique()
            .HasFilter("\"OctorateTransactionId\" IS NOT NULL")
            .HasDatabaseName(Payment.OctorateTransactionIndexName);

        // ─── Check-in to Octorate (NF-41, NF-45, E03/F01/US01/T03) ───────────────
        // The guests sent (one per booking and position) and the states of a stay (one per booking and state); the tenant query filter is
        // applied below, to every ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new StayGuestOctorateLinkConfiguration());
        modelBuilder.ApplyConfiguration(new OctorateStayStateSyncConfiguration());

        // ─── Internal activities: the staff work (NF-22) and the "To do" list (ADR-026) ───
        // An organization-level To do has no property, so the property is optional; deleting a property still deletes its
        // activities, as when it was required. The action is stored by name (the members are appended by later tasks).
        // The list reads the open To do rows of an org (Action set, CompletedAt null) on every page of the shell; the unique index
        // keeps one open item per type and entity, so a second CreateAsync, even a parallel one, returns the first.
        modelBuilder.Entity<InternalActivity>(entity =>
        {
            entity.HasOne(a => a.Property)
                .WithMany()
                .HasForeignKey(a => a.PropertyId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(a => a.Action)
                .HasConversion<string>()
                .HasMaxLength(50);
            // Kept on its own: the two indexes below are partial (open To do rows only), so they cannot serve the staff rows,
            // the closed items or the cascade of an org.
            entity.HasIndex(a => a.OrgId);
            entity.HasIndex(a => new { a.OrgId, a.Urgency, a.Deadline })
                .HasFilter("\"Action\" IS NOT NULL AND \"CompletedAt\" IS NULL")
                .HasDatabaseName("IX_InternalActivities_OpenTodo");
            entity.HasIndex(a => new { a.OrgId, a.ActivityType, a.EntityType, a.EntityId })
                .IsUnique()
                .HasFilter("\"Action\" IS NOT NULL AND \"CompletedAt\" IS NULL")
                .HasDatabaseName(InternalActivity.OpenTodoIndexName);
        });

        // ─── Drafts of the wizards (ADR-024) ─────────────────────────────────────
        // One draft per person, wizard and entity. The unique indexes are what makes two devices that save the first draft at the same
        // moment lose to each other (the loser gets 23505 and is answered as a conflict). The database takes two NULLs for different
        // values, so the draft of a new entity (no EntityId) has an index of its own. The anonymized rows (UserId = '') are left out of
        // both: they all share the empty UserId, and two of them for the same org, wizard and entity would fail the retention job.
        // xmin is the concurrency token: two parallel saves of the same version cannot both win. The index on ExpiresAt serves the
        // retention, which reads the drafts that are over and not yet anonymized.
        modelBuilder.Entity<WizardDraft>(entity =>
        {
            entity.Property(d => d.RowVersion).IsRowVersion();
            entity.HasIndex(d => new { d.OrgId, d.UserId, d.WizardType, d.EntityId })
                .IsUnique()
                .HasFilter("\"EntityId\" IS NOT NULL AND \"UserId\" <> ''")
                .HasDatabaseName(WizardDraft.ExistingEntityIndexName);
            entity.HasIndex(d => new { d.OrgId, d.UserId, d.WizardType })
                .IsUnique()
                .HasFilter("\"EntityId\" IS NULL AND \"UserId\" <> ''")
                .HasDatabaseName(WizardDraft.NewEntityIndexName);
            entity.HasIndex(d => d.ExpiresAt)
                .HasFilter("\"UserId\" <> ''");
        });

        // ─── Tourist tax paid to the comuni (NF-100, REP-05) ───────────────────────
        // The «paid» half of the summary per comune. A comune can be paid in instalments, so the payments of the same comune and period are
        // not unique: the index only serves the summary, which reads the payments of an org by comune and by the period they lie in. A
        // fiscal proof must not disappear with a row of the org (RESTRICT), and the amount and the period are checked by the database too:
        // a payment is above zero and starts no later than it ends.
        modelBuilder.Entity<TouristTaxRemittance>(entity =>
        {
            entity.HasOne<Org>()
                .WithMany()
                .HasForeignKey(r => r.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(r => new { r.OrgId, r.ComuneKey, r.PeriodFrom, r.PeriodTo });
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_TouristTaxRemittances_PaidAmountPositive", "\"PaidAmount\" > 0");
                t.HasCheckConstraint("CK_TouristTaxRemittances_PeriodOrder", "\"PeriodFrom\" <= \"PeriodTo\"");
            });
        });

        // ─── Tourist tax per guest (NF-46, ADR-037) ──────────────────────────────
        // Reference data (exemption reasons, seeded; channel rules, never seeded), the host-declared rates and the confirmed stay record with
        // its lines. The tenant query filter is applied below, to every ITenantOwned entity.
        modelBuilder.ApplyConfiguration(new TouristTaxExemptionReasonConfiguration());
        modelBuilder.ApplyConfiguration(new TouristTaxChannelRuleConfiguration());
        modelBuilder.ApplyConfiguration(new OrgTouristTaxRateConfiguration());
        modelBuilder.ApplyConfiguration(new StayTouristTaxConfiguration());
        modelBuilder.ApplyConfiguration(new StayTouristTaxGuestConfiguration());

        // ─── Marketplace (E09/F01, NF-54, NF-56, NF-57, NF-59) ───────────────────
        // The portals of the channel manager are platform data refreshed once a day, unique by the code Octorate gives them.
        modelBuilder.Entity<MarketplacePortal>(entity =>
        {
            entity.HasIndex(p => p.PortalCode)
                .IsUnique()
                .HasDatabaseName(MarketplacePortal.PortalCodeIndexName);
        });

        // A partner is a row the platform admin enters: unique by its slug, read in the order of its work. Its texts go with it. The checks
        // keep a code that is not a lowercase slug and a site that is not https out of the table, whoever writes the row.
        modelBuilder.Entity<MarketplacePartner>(entity =>
        {
            entity.HasIndex(p => p.Code)
                .IsUnique()
                .HasDatabaseName(MarketplacePartner.CodeIndexName);
            entity.HasIndex(p => new { p.Job, p.SortOrder });
            entity.HasMany(p => p.Texts)
                .WithOne()
                .HasForeignKey(t => t.PartnerId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.ToTable(t =>
            {
                t.HasCheckConstraint(MarketplacePartner.CodeSlugCheckName, "\"Code\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                t.HasCheckConstraint(MarketplacePartner.WebsiteHttpsCheckName, "\"WebsiteUrl\" LIKE 'https://%'");
            });
        });

        // One row of texts per partner and language (it or en).
        modelBuilder.Entity<MarketplacePartnerText>(entity =>
        {
            entity.Property(t => t.FaqJson).HasColumnType("jsonb");
            entity.HasIndex(t => new { t.PartnerId, t.Language })
                .IsUnique()
                .HasDatabaseName(MarketplacePartnerText.PartnerLanguageIndexName);
            entity.ToTable(t => t.HasCheckConstraint(MarketplacePartnerText.LanguageCheckName, "\"Language\" IN ('it', 'en')"));
        });

        // What an org activated. A channel or a tool is per property, a licensed function and a partner per org: two partial unique indexes
        // (the database takes two NULLs for different values, so one index would let a second org-level row in). An activation goes with its
        // property (CASCADE; a property is soft-deleted, so in practice it stays). xmin is the concurrency token. The tenant query filter
        // is applied below, to every ITenantOwned entity.
        modelBuilder.Entity<MarketplaceActivation>(entity =>
        {
            entity.Property(a => a.RowVersion).IsRowVersion();
            entity.HasOne<Property>()
                .WithMany()
                .HasForeignKey(a => a.PropertyId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(a => new { a.OrgId, a.Kind, a.TargetCode })
                .IsUnique()
                .HasFilter("\"PropertyId\" IS NULL")
                .HasDatabaseName(MarketplaceActivation.OrgLevelIndexName);
            entity.HasIndex(a => new { a.OrgId, a.PropertyId, a.Kind, a.TargetCode })
                .IsUnique()
                .HasFilter("\"PropertyId\" IS NOT NULL")
                .HasDatabaseName(MarketplaceActivation.PropertyLevelIndexName);
            entity.HasIndex(a => new { a.OrgId, a.Status });
        });

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

        // PC-05, A2-18: soft-deleted properties never appear in a normal read. A separate named filter (not the
        // tenant one) so it composes independently: IgnoreQueryFilters([TenantQueryFilter]) (entitlement counts,
        // admin cross-org reads) still excludes deleted properties, and IgnoreQueryFilters([SoftDeleteQueryFilter])
        // (fiscal/compliance reporting) still respects tenant isolation.
        modelBuilder.Entity<Property>().HasQueryFilter(SoftDeleteQueryFilter, p => !p.IsDeleted);

        // UI-13a: the stored search keys and their indexes (global search, docs/runbooks/global-search.md).
        SearchKeyModel.Configure(modelBuilder);

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>Key of the global tenant query filter, for <c>IgnoreQueryFilters([TenantQueryFilter])</c>.</summary>
    public const string TenantQueryFilter = "Tenant";

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
