using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data.Encryption;
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

    // Official ISTAT list of the comuni and the log of its imports (SU-04)
    public DbSet<Comune> Comuni { get; set; } = null!;
    public DbSet<ComuneImport> ComuneImports { get; set; } = null!;
    public DbSet<CancellationPolicy> CancellationPolicies { get; set; } = null!;
    public DbSet<PricingAdapterConfig> PricingAdapterConfigs { get; set; } = null!;
    public DbSet<PricingHistory> PricingHistories { get; set; } = null!;
    public DbSet<SeasonalPriceSuggestion> SeasonalPriceSuggestions { get; set; } = null!;
    public DbSet<PropertyDocument> PropertyDocuments { get; set; } = null!;
    public DbSet<SeoContentPage> SeoContentPages { get; set; } = null!;
    public DbSet<SeoContentRevision> SeoContentRevisions { get; set; } = null!;
    public DbSet<SeoContentReviewEvent> SeoContentReviewEvents { get; set; } = null!;
    public DbSet<PlatformAiBudget> PlatformAiBudgets { get; set; } = null!;
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
    /// The supplier's agenda (SP-03): weekly working hours, time off, blocks and extra openings, and the settings row. All
    /// keyed by the supplier org and <b>not</b> tenant-filtered (see the TN-2 allow-list): only <c>SupplierAgendaService</c>
    /// (and the supplier repair) reads and writes them, always with an explicit <c>OrgId</c> predicate.
    /// </summary>
    public DbSet<SupplierWorkingHours> SupplierWorkingHours { get; set; } = null!;
    public DbSet<SupplierTimeOff> SupplierTimeOff { get; set; } = null!;
    public DbSet<SupplierBusyWindow> SupplierBusyWindows { get; set; } = null!;
    public DbSet<SupplierSettings> SupplierSettings { get; set; } = null!;
    public DbSet<SupplierInviteRecord> SupplierInviteRecords { get; set; } = null!;
    public DbSet<SupplierAdminAuditEntry> SupplierAdminAuditEntries { get; set; } = null!;
    public DbSet<ServiceRequest> ServiceRequests { get; set; } = null!;

    /// <summary>
    /// The booking from a supplier's public showcase (SP-10): the private customers and the holds that wait for the e-mail check.
    /// Both keyed by the supplier org and <b>not</b> tenant-filtered (see the TN-2 allow-list); only the booking service and the
    /// few readers listed in <c>ShowcaseBookingTenancyTests</c> touch them, always with an explicit <c>OrgId</c> predicate.
    /// </summary>
    public DbSet<ServiceCustomer> ServiceCustomers { get; set; } = null!;
    public DbSet<ShowcaseBookingHold> ShowcaseBookingHolds { get; set; } = null!;

    // Property iCal OTA sync (US-018 / #294)
    public DbSet<CalendarBlock> CalendarBlocks { get; set; } = null!;
    public DbSet<PropertyICalFeed> PropertyICalFeeds { get; set; } = null!;
    public DbSet<PropertyICalExport> PropertyICalExports { get; set; } = null!;

    // Scheduled changes of the rental mode of a property, and their history (PM-02)
    public DbSet<PropertyModeChange> PropertyModeChanges { get; set; } = null!;

    // Guest self-service check-in portal (US-020 / #296)
    public DbSet<GuestCheckInSession> GuestCheckInSessions { get; set; } = null!;

    // Native host app push tokens (US-025 / #299)
    public DbSet<DeviceRegistration> DeviceRegistrations { get; set; } = null!;

    // Push messages per event and device, with their Expo ticket (MO-04)
    public DbSet<PushDelivery> PushDeliveries { get; set; } = null!;

    public DbSet<TerritorialRentAgreement> TerritorialRentAgreements { get; set; } = null!;
    public DbSet<ConcordatoRentBand> ConcordatoRentBands { get; set; } = null!;
    public DbSet<TerritorialAgreementSignatory> TerritorialAgreementSignatories { get; set; } = null!;
    public DbSet<HighTensionAreaComune> HighTensionAreaComuni { get; set; } = null!;
    public DbSet<ComuneImuChannel> ComuneImuChannels { get; set; } = null!;
    public DbSet<RegulatoryDataAuditEntry> RegulatoryDataAuditEntries { get; set; } = null!;

    // Long-term lease
    public DbSet<LeaseContract> LeaseContracts { get; set; } = null!;
    public DbSet<Party> Parties { get; set; } = null!;
    public DbSet<LeaseRegistration> LeaseRegistrations { get; set; } = null!;
    public DbSet<LeaseEvent> LeaseEvents { get; set; } = null!;
    public DbSet<LeaseRegistrationAuthorization> LeaseRegistrationAuthorizations { get; set; } = null!;
    public DbSet<LeaseSigner> LeaseSigners { get; set; } = null!;
    public DbSet<RentSchedule> RentSchedules { get; set; } = null!;
    public DbSet<RentLedgerEntry> RentLedgerEntries { get; set; } = null!;
    public DbSet<AppContextEntity> AppContexts { get; set; } = null!;
    public DbSet<ConsentRecord> ConsentRecords { get; set; } = null!;
    public DbSet<GuestConsentRecord> GuestConsentRecords { get; set; } = null!;
    public DbSet<GuestPrivacyAuditEntry> GuestPrivacyAuditEntries { get; set; } = null!;
    public DbSet<SignupAttribution> SignupAttributions { get; set; } = null!;
    public DbSet<SeoEvent> SeoEvents { get; set; } = null!;
    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<RolePermission> RolePermissions { get; set; } = null!;
    public DbSet<UserContextMembership> UserContextMemberships { get; set; } = null!;

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

        // CO-10: the alert stages of a stay go with it.
        modelBuilder.Entity<StayAlertState>()
            .HasOne(s => s.Booking)
            .WithMany()
            .HasForeignKey(s => s.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        // CO-17: the check-out of a stay goes with it; its cleaning request survives it as a plain request.
        modelBuilder.Entity<StayCheckout>(entity =>
        {
            entity.HasOne(c => c.Booking)
                .WithMany()
                .HasForeignKey(c => c.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.CleaningRequest)
                .WithMany()
                .HasForeignKey(c => c.CleaningRequestId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // CO-20: the CIN alert stage of a property goes with it.
        modelBuilder.Entity<CinAlertState>()
            .HasOne(s => s.Property)
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        // CO-12: the guests of a stay follow their booking; the booker link survives the booker's deletion as null.
        modelBuilder.Entity<StayGuest>(entity =>
        {
            entity.HasOne(s => s.Booking)
                .WithMany(b => b.StayGuests)
                .HasForeignKey(s => s.BookingId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(s => s.Guest)
                .WithMany()
                .HasForeignKey(s => s.GuestId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(s => s.OrgId);
            entity.HasIndex(s => s.GuestId);
        });

        // CO-15: privacy history of a guest (notice presented, marketing consent), removed with the guest record.
        modelBuilder.Entity<GuestConsentRecord>(entity =>
        {
            entity.HasOne(r => r.Guest)
                .WithMany()
                .HasForeignKey(r => r.GuestId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(r => r.OrgId);
            entity.HasIndex(r => new { r.GuestId, r.Purpose, r.RecordedAt });
        });

        // CO-15: audit of the operations on guest data; no foreign key to the guest, so it outlives a removed guest.
        modelBuilder.Entity<GuestPrivacyAuditEntry>(entity =>
        {
            entity.HasOne<Org>().WithMany().HasForeignKey(a => a.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(a => a.OrgId);
            entity.HasIndex(a => new { a.GuestId, a.OccurredAt });
        });

        // CO-07: one safety checklist per property, going with it; its items go with the checklist. An evidence
        // document deleted by the host leaves the item without proof (SET NULL), never deletes the answer.
        modelBuilder.Entity<PropertySafetyChecklist>(entity =>
        {
            entity.HasOne(c => c.Property)
                .WithOne()
                .HasForeignKey<PropertySafetyChecklist>(c => c.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => c.PropertyId).IsUnique();
            entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(c => c.OrgId);
            entity.HasMany(c => c.Items)
                .WithOne(i => i.Checklist)
                .HasForeignKey(i => i.ChecklistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PropertySafetyChecklistItem>(entity =>
        {
            entity.HasIndex(i => new { i.ChecklistId, i.Code }).IsUnique();
            entity.HasOne(i => i.EvidenceDocument)
                .WithMany()
                .HasForeignKey(i => i.EvidenceDocumentId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(i => i.EvidenceDocumentId);
            entity.HasOne<Org>().WithMany().HasForeignKey(i => i.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(i => i.OrgId);
        });

        modelBuilder.Entity<AlloggiatiCodeEntry>()
            .HasOne(e => e.Import)
            .WithMany()
            .HasForeignKey(e => e.ImportId)
            .OnDelete(DeleteBehavior.Restrict);

        // SU-04: official ISTAT list of the comuni. The ISTAT code is the key; the cadastral code is unique among the active
        // comuni only (a comune that changes province gets a new ISTAT code and keeps its cadastral code, so the old row,
        // deactivated, and the new one coexist).
        modelBuilder.Entity<Comune>(entity =>
        {
            entity.HasIndex(c => c.CadastralCode)
                .IsUnique()
                .HasFilter("\"IsActive\"")
                .HasDatabaseName("IX_Comuni_CadastralCode_Active");
            entity.HasIndex(c => c.CadastralCode);
            entity.HasIndex(c => c.NormalizedName);
            entity.HasIndex(c => c.ProvinceCode);
            entity.HasIndex(c => c.RegionIstatCode);
            entity.HasOne(c => c.SourceImport)
                .WithMany()
                .HasForeignKey(c => c.SourceImportId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(c => c.SourceImportId);
        });
        modelBuilder.Entity<ComuneImport>(entity =>
        {
            entity.HasIndex(i => i.ImportedAt);
            entity.HasIndex(i => i.ReferenceDate);
        });

        // CO-14: one set of Alloggiati Web credentials per property, going with it; tenant row (TN-2).
        modelBuilder.Entity<PropertyQuesturaCredentials>(entity =>
        {
            entity.HasOne(c => c.Property)
                .WithMany()
                .HasForeignKey(c => c.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(c => c.PropertyId).IsUnique();
            entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(c => c.OrgId);
        });

        // "Le mie prenotazioni" finds a booking by the org of the site and its code (BK-11).
        modelBuilder.Entity<Booking>()
            .HasIndex(b => new { b.OrgId, b.BookingCode })
            .IsUnique();

        // GPS coordinates (PC-06, A2-33): 6 decimals are about 0.1 m, the old 2 decimals placed a house up to a kilometre
        // away on the map. numeric(9,6) holds -999.999999..999.999999: the API accepts only -90..90 and -180..180.
        modelBuilder.Entity<Property>()
            .Property(p => p.Latitude)
            .HasPrecision(9, PropertyAddress.CoordinateScale);

        modelBuilder.Entity<Property>()
            .Property(p => p.Longitude)
            .HasPrecision(9, PropertyAddress.CoordinateScale);

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
        modelBuilder.Entity<PaymentRefund>(refund =>
        {
            refund.HasIndex(r => r.PaymentId);
            refund.HasIndex(r => r.OrgId);
            refund.HasIndex(r => r.StripeRefundId)
                .IsUnique()
                .HasFilter("\"StripeRefundId\" IS NOT NULL");
            refund.HasIndex(r => r.IdempotencyKey).IsUnique();
            refund.HasOne(r => r.Payment)
                .WithMany()
                .HasForeignKey(r => r.PaymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
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
        modelBuilder.Entity<SeasonalPriceSuggestion>(entity =>
        {
            entity.HasOne<Property>()
                .WithMany()
                .HasForeignKey(s => s.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(s => new { s.PropertyId, s.StayDate }).IsUnique();
            entity.HasIndex(s => s.OrgId);
            entity.Property(s => s.Rule).HasConversion<string>();
            entity.Property(s => s.Holiday).HasConversion<string>();
        });

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

        // ─── Multi-tenant Org boundary (US-004) ──────────────────────────────────
        // Org tenant key with a unique Slug (AC1).
        modelBuilder.Entity<Org>()
            .HasIndex(o => o.Slug)
            .IsUnique();

        modelBuilder.Entity<Org>()
            .HasIndex(o => o.StripeCustomerId);

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
        modelBuilder.Entity<OrgSiteDocument>(entity =>
        {
            entity.HasOne(d => d.Org)
                .WithMany()
                .HasForeignKey(d => d.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            entity.Property(d => d.Source).HasConversion<string>().HasMaxLength(20);

            entity.HasIndex(d => new { d.OrgId, d.Kind, d.Version })
                .IsUnique()
                .HasDatabaseName("UIX_OrgSiteDocuments_Org_Kind_Version");
        });

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

        modelBuilder.Entity<ConsentRecord>()
            .HasIndex(c => new { c.UserId, c.OrgId, c.Type });

        // ─── Signup attribution (SE-03 / A8-03) ─────────────────────────────────
        modelBuilder.Entity<SignupAttribution>(entity =>
        {
            entity.HasOne<Org>()
                .WithMany()
                .HasForeignKey(a => a.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            // One attribution per org: the first one wins, a parallel insert fails with 23505 and is ignored.
            entity.HasIndex(a => a.OrgId)
                .IsUnique()
                .HasDatabaseName("UIX_SignupAttributions_OrgId");

            entity.HasIndex(a => a.RecordedAt)
                .HasDatabaseName("IX_SignupAttributions_RecordedAt");
        });

        // SEO funnel events (SE-04): platform data without an org or a person; the report groups by comune over a window,
        // the nightly retention deletes by date.
        modelBuilder.Entity<SeoEvent>(entity =>
        {
            entity.HasIndex(e => e.OccurredAt)
                .HasDatabaseName("IX_SeoEvents_OccurredAt");

            entity.HasIndex(e => new { e.ComuneCode, e.OccurredAt })
                .HasDatabaseName("IX_SeoEvents_ComuneCode_OccurredAt");
        });

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
        modelBuilder.Entity<SupplierServiceListing>(entity =>
        {
            entity.HasOne(l => l.SupplierProfile)
                .WithMany()
                .HasForeignKey(l => l.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(l => l.Version).IsRowVersion();

            entity.HasIndex(l => new { l.OrgId, l.Slug })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL")
                .HasDatabaseName("UIX_SupplierServiceListings_OrgId_Slug");

            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_SupplierServiceListings_PriceFromCents", "\"PriceFromCents\" IS NULL OR \"PriceFromCents\" > 0");
                t.HasCheckConstraint(
                    "CK_SupplierServiceListings_DurationMinutes", "\"DurationMinutes\" IS NULL OR \"DurationMinutes\" > 0");
                t.HasCheckConstraint(
                    "CK_SupplierServiceListings_MinNoticeHours", "\"MinNoticeHours\" IS NULL OR \"MinNoticeHours\" >= 0");
                t.HasCheckConstraint(
                    "CK_SupplierServiceListings_WeekdaysMask", "\"WeekdaysMask\" BETWEEN 0 AND 127");
            });
        });

        // SP-03: the supplier's agenda. Children of the supplier profile in cascade, like the availability days and the
        // catalog: the repair moves them to the keeper before it deletes a duplicate profile. The checks mirror
        // SupplierAgendaRules / SupplierAgendaLimits (the rules refuse first; the database is the last guard).
        modelBuilder.Entity<SupplierWorkingHours>(entity =>
        {
            entity.HasOne(h => h.SupplierProfile)
                .WithMany()
                .HasForeignKey(h => h.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            // One band per weekday and start: the same band twice is never meant. The overlap of two bands and the limit of
            // three a day need more than a unique index: the service decides both under the agenda lock.
            entity.HasIndex(h => new { h.OrgId, h.Weekday, h.StartMinute })
                .IsUnique()
                .HasDatabaseName("UIX_SupplierWorkingHours_OrgId_Weekday_StartMinute");

            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SupplierWorkingHours_Weekday", "\"Weekday\" BETWEEN 0 AND 6");
                t.HasCheckConstraint(
                    "CK_SupplierWorkingHours_Minutes",
                    $"\"StartMinute\" >= 0 AND \"StartMinute\" < \"EndMinute\" AND \"EndMinute\" <= {SupplierAgendaLimits.MinutesPerDay}");
            });
        });

        modelBuilder.Entity<SupplierTimeOff>(entity =>
        {
            entity.HasOne(t => t.SupplierProfile)
                .WithMany()
                .HasForeignKey(t => t.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(t => new { t.OrgId, t.FromDate })
                .HasDatabaseName("IX_SupplierTimeOff_OrgId_FromDate");

            entity.ToTable(t => t.HasCheckConstraint("CK_SupplierTimeOff_Dates", "\"FromDate\" <= \"ToDate\""));
        });

        modelBuilder.Entity<SupplierBusyWindow>(entity =>
        {
            entity.HasOne(w => w.SupplierProfile)
                .WithMany()
                .HasForeignKey(w => w.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            // The planner and the calendar read the windows of one supplier by time; the iCal sync (SP-05) adds its own
            // unique index on the event it writes.
            entity.HasIndex(w => new { w.OrgId, w.StartUtc })
                .HasDatabaseName("IX_SupplierBusyWindows_OrgId_StartUtc");

            entity.ToTable(t => t.HasCheckConstraint("CK_SupplierBusyWindows_Interval", "\"StartUtc\" < \"EndUtc\""));
        });

        modelBuilder.Entity<SupplierSettings>(entity =>
        {
            // One row per supplier: the key is the supplier org, which is also the foreign key.
            entity.HasOne(s => s.SupplierProfile)
                .WithOne()
                .HasForeignKey<SupplierSettings>(s => s.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            // The bounds are the ones of SupplierAgendaLimits (a constant, so the rules and the database cannot drift).
            entity.ToTable(t =>
            {
                t.HasCheckConstraint(
                    "CK_SupplierSettings_BufferMinutes",
                    $"\"BufferMinutes\" BETWEEN {SupplierAgendaLimits.MinBufferMinutes} AND {SupplierAgendaLimits.MaxBufferMinutes}");
                t.HasCheckConstraint(
                    "CK_SupplierSettings_MaxJobsPerDay",
                    $"\"MaxJobsPerDay\" BETWEEN {SupplierAgendaLimits.MinJobsPerDay} AND {SupplierAgendaLimits.MaxJobsPerDayLimit}");
                t.HasCheckConstraint(
                    "CK_SupplierSettings_MinNoticeHours",
                    $"\"MinNoticeHours\" BETWEEN {SupplierAgendaLimits.MinNoticeHours} AND {SupplierAgendaLimits.MaxNoticeHours}");
                t.HasCheckConstraint(
                    "CK_SupplierSettings_HorizonDays",
                    $"\"HorizonDays\" BETWEEN {SupplierAgendaLimits.MinHorizonDays} AND {SupplierAgendaLimits.MaxHorizonDays}");
                t.HasCheckConstraint(
                    "CK_SupplierSettings_SlotStepMinutes",
                    $"\"SlotStepMinutes\" BETWEEN {SupplierAgendaLimits.MinSlotStepMinutes} AND {SupplierAgendaLimits.MaxSlotStepMinutes}");
                t.HasCheckConstraint(
                    "CK_SupplierSettings_ParallelJobs",
                    $"\"ParallelJobs\" BETWEEN {SupplierAgendaLimits.MinParallelJobs} AND {SupplierAgendaLimits.MaxParallelJobs}");
                t.HasCheckConstraint(
                    "CK_SupplierSettings_RespondWithinMinutes",
                    $"\"RespondWithinMinutes\" BETWEEN {SupplierAgendaLimits.MinRespondWithinMinutes} AND {SupplierAgendaLimits.MaxRespondWithinMinutes}");
            });
        });

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
        modelBuilder.Entity<ServiceRequest>(entity =>
        {
            entity.Property(sr => sr.OptionsJson).HasDefaultValue("[]");
            entity.Property(sr => sr.PriceLinesJson).HasDefaultValue("[]");
            entity.Property(sr => sr.WorkPhotosJson).HasDefaultValue("[]");
        });

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
        modelBuilder.Entity<ServiceRequest>(entity =>
        {
            entity.HasOne(sr => sr.Customer)
                .WithMany()
                .HasForeignKey(sr => sr.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(sr => sr.CustomerId).HasDatabaseName("IX_ServiceRequests_CustomerId");

            // The code is unique for the supplier among its showcase requests (the host requests have none).
            entity.HasIndex(sr => new { sr.SupplierOrgId, sr.PublicCode })
                .IsUnique()
                .HasFilter("\"PublicCode\" IS NOT NULL")
                .HasDatabaseName("UIX_ServiceRequests_SupplierOrgId_PublicCode");

            entity.ToTable(t => t.HasCheckConstraint(
                "CK_ServiceRequests_Context",
                $"(\"RentalContext\" = {(int)ServiceRequestRentalContext.Showcase} "
                + "AND \"PropertyId\" IS NULL AND \"BookingId\" IS NULL "
                + $"AND \"Source\" = {(int)ServiceRequestSource.Showcase} "
                + "AND \"CustomerId\" IS NOT NULL AND \"PublicCode\" IS NOT NULL AND \"LocationCity\" IS NOT NULL) OR "
                + $"(\"RentalContext\" IN ({(int)ServiceRequestRentalContext.ShortRent}, {(int)ServiceRequestRentalContext.LongRent}) "
                + "AND \"PropertyId\" IS NOT NULL "
                + $"AND \"Source\" = {(int)ServiceRequestSource.Host} "
                + "AND \"CustomerId\" IS NULL AND \"PublicCode\" IS NULL "
                + "AND \"LocationComuneIstat\" IS NULL AND \"LocationCity\" IS NULL AND \"LocationPostalCode\" IS NULL "
                + "AND \"LocationAddress\" IS NULL AND \"LocationFloor\" IS NULL AND \"LocationAccessNotes\" IS NULL)"));
        });

        modelBuilder.Entity<ServiceCustomer>(entity =>
        {
            // A child of the supplier profile like the agenda: the repair moves the customers before it deletes a profile.
            entity.HasOne(c => c.SupplierProfile)
                .WithMany()
                .HasForeignKey(c => c.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            // One customer per supplier and address; the HMAC of the address is the only thing the address is found by.
            entity.HasIndex(c => new { c.OrgId, c.EmailHash })
                .IsUnique()
                .HasDatabaseName("UIX_ServiceCustomers_OrgId_EmailHash");
        });

        modelBuilder.Entity<ShowcaseBookingHold>(entity =>
        {
            entity.HasOne(h => h.SupplierProfile)
                .WithMany()
                .HasForeignKey(h => h.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            // The request born from the hold. Set to null if the request is ever removed for good: the hold only remembers it.
            entity.HasOne<ServiceRequest>()
                .WithMany()
                .HasForeignKey(h => h.ServiceRequestId)
                .OnDelete(DeleteBehavior.SetNull);

            // The column says what it holds: encrypted at rest through the value converter of EncryptedColumns.
            entity.Property(h => h.Payload).HasColumnName("PayloadEncrypted");

            // The same client request id is the same hold (idempotency); a code is unique among the supplier's holds.
            entity.HasIndex(h => new { h.OrgId, h.ClientRequestId })
                .IsUnique()
                .HasDatabaseName("UIX_ShowcaseBookingHolds_OrgId_ClientRequestId");
            entity.HasIndex(h => new { h.OrgId, h.PublicCode })
                .IsUnique()
                .HasDatabaseName("UIX_ShowcaseBookingHolds_OrgId_PublicCode");

            // The planner reads the holds of one supplier by time; the upkeep job reads the expired ones of every supplier; the
            // cap of unverified bookings counts the ones of one address.
            entity.HasIndex(h => new { h.OrgId, h.StartUtc }).HasDatabaseName("IX_ShowcaseBookingHolds_OrgId_StartUtc");
            entity.HasIndex(h => h.ExpiresAt).HasDatabaseName("IX_ShowcaseBookingHolds_ExpiresAt");
            entity.HasIndex(h => new { h.OrgId, h.EmailHash }).HasDatabaseName("IX_ShowcaseBookingHolds_OrgId_EmailHash");

            entity.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ShowcaseBookingHolds_Interval", "\"StartUtc\" < \"EndUtc\"");
                t.HasCheckConstraint("CK_ShowcaseBookingHolds_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");
            });
        });

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
        modelBuilder.Entity<PropertyModeChange>(entity =>
        {
            // The history goes with the property (a property is soft-deleted, so in practice it stays for good).
            entity.HasOne(c => c.Property)
                .WithMany()
                .HasForeignKey(c => c.PropertyId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Org>()
                .WithMany()
                .HasForeignKey(c => c.OrgId)
                .OnDelete(DeleteBehavior.Restrict);

            // One change waiting for its day per property. The creation takes the property dates lock first, so this is the
            // net under it: a writer that skipped the lock gets 23505 on this index and answers property_mode_change_exists.
            entity.HasIndex(c => c.PropertyId)
                .IsUnique()
                .HasFilter($"\"Status\" = {(int)PropertyModeChangeStatus.Scheduled}")
                .HasDatabaseName(PropertyModeChange.OneScheduledIndexName);

            // The hourly job: the scheduled changes whose day has come.
            entity.HasIndex(c => new { c.Status, c.EffectiveDate });

            // The history of a property, newest first.
            entity.HasIndex(c => new { c.PropertyId, c.CreatedAt });
        });

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

        // Guest check-in session (US-020 / #296)
        modelBuilder.Entity<GuestCheckInSession>(entity =>
        {
            entity.HasOne(s => s.Booking)
                .WithMany()
                .HasForeignKey(s => s.BookingId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(s => s.TokenHash)
                .IsUnique()
                .HasDatabaseName("UIX_GuestCheckInSessions_TokenHash");

            // Only one active session per booking at a time (statuses 0-3 are active)
            entity.HasIndex(s => new { s.BookingId, s.Status })
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1, 2, 3)")
                .HasDatabaseName("UIX_GuestCheckInSessions_BookingId_ActiveStatus");
        });

        modelBuilder.Entity<TerritorialRentAgreement>(entity =>
        {
            entity.HasIndex(a => a.Comune);
            entity.HasMany(a => a.Bands)
                .WithOne(b => b.Agreement)
                .HasForeignKey(b => b.TerritorialRentAgreementId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(a => a.Signatories)
                .WithOne(s => s.Agreement)
                .HasForeignKey(s => s.TerritorialRentAgreementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HighTensionAreaComune>()
            .HasIndex(c => c.Comune);

        modelBuilder.Entity<ComuneImuChannel>()
            .HasIndex(c => c.Comune);

        modelBuilder.Entity<RegulatoryDataAuditEntry>()
            .HasIndex(e => new { e.EntityId, e.OccurredAt });

        // Native host app push tokens (US-025 / #299)
        modelBuilder.Entity<DeviceRegistration>(entity =>
        {
            entity.HasOne(d => d.Org)
                .WithMany()
                .HasForeignKey(d => d.OrgId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(d => new { d.UserId, d.DeviceId })
                .IsUnique()
                .HasDatabaseName("UIX_DeviceRegistrations_UserId_DeviceId");

            entity.HasIndex(d => d.UserId)
                .HasDatabaseName("IX_DeviceRegistrations_UserId");
        });

        // PC-05, A2-18: soft-deleted properties never appear in a normal read. A separate named filter (not the
        // tenant one) so it composes independently: IgnoreQueryFilters([TenantQueryFilter]) (entitlement counts,
        // admin cross-org reads) still excludes deleted properties, and IgnoreQueryFilters([SoftDeleteQueryFilter])
        // (fiscal/compliance reporting) still respects tenant isolation.
        modelBuilder.Entity<Property>().HasQueryFilter(SoftDeleteQueryFilter, p => !p.IsDeleted);

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
