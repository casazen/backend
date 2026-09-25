using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

    // D.L. 145/2023 safety checklist of a short-stay property (CO-07)
    public DbSet<PropertySafetyChecklist> PropertySafetyChecklists { get; set; } = null!;
    public DbSet<PropertySafetyChecklistItem> PropertySafetyChecklistItems { get; set; } = null!;

    // Guests of a stay and official Alloggiati code tables (CO-12)
    public DbSet<StayGuest> StayGuests { get; set; } = null!;
    public DbSet<AlloggiatiCodeEntry> AlloggiatiCodeEntries { get; set; } = null!;
    public DbSet<AlloggiatiCodeTableImport> AlloggiatiCodeTableImports { get; set; } = null!;
    public DbSet<PropertyQuesturaCredentials> PropertyQuesturaCredentials { get; set; } = null!;
    public DbSet<CancellationPolicy> CancellationPolicies { get; set; } = null!;
    public DbSet<PricingAdapterConfig> PricingAdapterConfigs { get; set; } = null!;
    public DbSet<PricingHistory> PricingHistories { get; set; } = null!;
    public DbSet<PropertyDocument> PropertyDocuments { get; set; } = null!;
    public DbSet<SeoContentPage> SeoContentPages { get; set; } = null!;
    public DbSet<SeoContentRevision> SeoContentRevisions { get; set; } = null!;
    public DbSet<PlatformAiBudget> PlatformAiBudgets { get; set; } = null!;
    public DbSet<PlatformInvoice> PlatformInvoices { get; set; } = null!;
    public DbSet<ProcessedStripeEvent> ProcessedStripeEvents { get; set; } = null!;
    public DbSet<PlatformBillingMetrics> PlatformBillingMetrics { get; set; } = null!;

    // Supplier console (US-022 / #292)
    public DbSet<SupplierProfile> SupplierProfiles { get; set; } = null!;
    public DbSet<SupplierAvailability> SupplierAvailability { get; set; } = null!;
    public DbSet<SupplierInviteRecord> SupplierInviteRecords { get; set; } = null!;
    public DbSet<SupplierJob> SupplierJobs { get; set; } = null!;
    public DbSet<ServiceRequest> ServiceRequests { get; set; } = null!;

    // Property iCal OTA sync (US-018 / #294)
    public DbSet<CalendarBlock> CalendarBlocks { get; set; } = null!;
    public DbSet<PropertyICalFeed> PropertyICalFeeds { get; set; } = null!;
    public DbSet<PropertyICalExport> PropertyICalExports { get; set; } = null!;

    // Guest self-service check-in portal (US-020 / #296)
    public DbSet<GuestCheckInSession> GuestCheckInSessions { get; set; } = null!;

    // Native host app push tokens (US-025 / #299)
    public DbSet<DeviceRegistration> DeviceRegistrations { get; set; } = null!;

    public DbSet<TerritorialRentAgreement> TerritorialRentAgreements { get; set; } = null!;
    public DbSet<ConcordatoRentBand> ConcordatoRentBands { get; set; } = null!;
    public DbSet<TerritorialAgreementSignatory> TerritorialAgreementSignatories { get; set; } = null!;
    public DbSet<HighTensionAreaComune> HighTensionAreaComuni { get; set; } = null!;

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
    public DbSet<SignupAttribution> SignupAttributions { get; set; } = null!;
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

        modelBuilder.Entity<PropertyQuesturaCredentials>()
            .HasOne(c => c.Property)
            .WithMany()
            .HasForeignKey(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PropertyQuesturaCredentials>()
            .HasIndex(c => c.PropertyId)
            .IsUnique();

        // "Le mie prenotazioni" finds a booking by the org of the site and its code (BK-11).
        modelBuilder.Entity<Booking>()
            .HasIndex(b => new { b.OrgId, b.BookingCode })
            .IsUnique();

        // Precision for GPS coordinates
        modelBuilder.Entity<Property>()
            .Property(p => p.Latitude)
            .HasPrecision(18, 2);

        modelBuilder.Entity<Property>()
            .Property(p => p.Longitude)
            .HasPrecision(18, 2);

        // Indexes
        modelBuilder.Entity<Property>().HasIndex(p => p.OwnerId);

        // Unique constraint on property address for active properties only
        // Allows soft-deleted properties to be re-created at same address
        modelBuilder.Entity<Property>()
            .HasIndex(p => new { p.Address, p.City, p.PostalCode, p.IsActive })
            .IsUnique()
            .HasFilter("\"IsActive\" = true");

        modelBuilder.Entity<Property>()
            .HasIndex(p => new { p.OrgId, p.Slug })
            .IsUnique()
            .HasFilter("\"Slug\" IS NOT NULL")
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

        if (EncryptionProvider is not null)
        {
            var encryptedConverter = new EncryptedStringConverter(
                EncryptionProvider,
                "Casazen.OtaIntegration.Secrets");

            modelBuilder.Entity<OtaIntegration>()
                .Property(o => o.ApiKey)
                .HasConversion(encryptedConverter);

            modelBuilder.Entity<OtaIntegration>()
                .Property(o => o.ApiSecret)
                .HasConversion(encryptedConverter);

            // iCal import URLs carry the OTA's secret token (A2-20, PC-11). URLs saved in clear before PC-11 are read
            // as they are until PropertyICalFeedUrlEncryption rewrites them at startup (a protected payload never
            // starts with a URL scheme).
            modelBuilder.Entity<PropertyICalFeed>()
                .Property(f => f.ImportUrl)
                .HasConversion((ValueConverter)new EncryptedStringConverter(
                    EncryptionProvider,
                    PropertyICalFeedUrlEncryption.Purpose,
                    PropertyICalFeedUrlEncryption.IsLegacyPlaintext));
        }

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

        modelBuilder.Entity<Party>()
            .HasIndex(p => new { p.LeaseContractId, p.Role });

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

        modelBuilder.Entity<PlatformBillingMetrics>()
            .Property(m => m.EuB2cCrossBorderRevenue)
            .HasPrecision(18, 2);

        modelBuilder.Entity<PlatformBillingMetrics>().HasData(
            new PlatformBillingMetrics
            {
                Id = 1,
                CalendarYear = 2026,
                EuB2cCrossBorderRevenue = 0m,
                OssThresholdReached = false,
                UpdatedAt = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc),
            });

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

        // ─── Supplier console (US-022 / #292) ────────────────────────────────────
        modelBuilder.Entity<SupplierProfile>()
            .HasOne(sp => sp.Org)
            .WithOne()
            .HasForeignKey<SupplierProfile>(sp => sp.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierProfile>()
            .HasIndex(sp => sp.Status);

        modelBuilder.Entity<SupplierProfile>()
            .HasIndex(sp => sp.ClaimTokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_SupplierProfiles_ClaimTokenHash");

        modelBuilder.Entity<SupplierAvailability>()
            .HasOne(sa => sa.SupplierProfile)
            .WithMany()
            .HasForeignKey(sa => sa.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupplierAvailability>()
            .HasIndex(sa => new { sa.OrgId, sa.Date })
            .IsUnique();

        modelBuilder.Entity<SupplierInviteRecord>()
            .HasIndex(i => i.Email);

        modelBuilder.Entity<SupplierInviteRecord>()
            .HasIndex(i => new { i.Email, i.IsUsed });

        modelBuilder.Entity<SupplierInviteRecord>()
            .HasIndex(i => i.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_SupplierInviteRecords_TokenHash");

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

        ApplyTenantQueryFilters(modelBuilder);
    }

    /// <summary>Key of the global tenant query filter, for <c>IgnoreQueryFilters([TenantQueryFilter])</c>.</summary>
    public const string TenantQueryFilter = "Tenant";

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
