using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Property = Casazen.Core.Entities.Property;
using AppContextEntity = Casazen.Core.Entities.AppContext;

namespace Casazen.Infrastructure.Data;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ITenantContext? tenantContext = null,
    IDataProtectionProvider? dataProtectionProvider = null) : DbContext(options)
{
    // Resolves the caller's OrgId for the global tenant query filter (AC7). Falls back to a
    // no-op (filter disabled) for design-time, background jobs, and unit tests.
    private readonly ITenantContext _tenant = tenantContext ?? NullTenantContext.Instance;

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Org> Orgs { get; set; } = null!;
    public DbSet<Property> Properties { get; set; } = null!;
    public DbSet<Booking> Bookings { get; set; } = null!;
    public DbSet<Guest> Guests { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<OtaIntegration> OtaIntegrations { get; set; } = null!;
    public DbSet<TouristTaxRate> TouristTaxRates { get; set; } = null!;
    public DbSet<OtaSyncLog> OtaSyncLogs { get; set; } = null!;
    public DbSet<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = null!;
    public DbSet<PropertyQuesturaCredentials> PropertyQuesturaCredentials { get; set; } = null!;
    public DbSet<TaxRate> TaxRates { get; set; } = null!;
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

    // Long-term lease
    public DbSet<LeaseContract> LeaseContracts { get; set; } = null!;
    public DbSet<Party> Parties { get; set; } = null!;
    public DbSet<LeaseRegistration> LeaseRegistrations { get; set; } = null!;
    public DbSet<LeaseEvent> LeaseEvents { get; set; } = null!;
    public DbSet<RentSchedule> RentSchedules { get; set; } = null!;
    public DbSet<RentLedgerEntry> RentLedgerEntries { get; set; } = null!;
    public DbSet<AppContextEntity> AppContexts { get; set; } = null!;
    public DbSet<ConsentRecord> ConsentRecords { get; set; } = null!;
    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<RolePermission> RolePermissions { get; set; } = null!;
    public DbSet<UserContextMembership> UserContextMemberships { get; set; } = null!;

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

        modelBuilder.Entity<PropertyQuesturaCredentials>()
            .HasOne(c => c.Property)
            .WithMany()
            .HasForeignKey(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PropertyQuesturaCredentials>()
            .HasIndex(c => c.PropertyId)
            .IsUnique();

        modelBuilder.Entity<Booking>()
            .HasIndex(b => b.CheckInToken)
            .IsUnique()
            .HasFilter("\"CheckInToken\" IS NOT NULL");

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

        modelBuilder.Entity<Booking>().HasIndex(b => b.PropertyId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.GuestId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.CheckInDate);
        modelBuilder.Entity<Booking>().HasIndex(b => b.Status);
        modelBuilder.Entity<Payment>().HasIndex(p => p.BookingId);
        modelBuilder.Entity<OtaIntegration>().HasIndex(o => o.PropertyId);

        if (dataProtectionProvider is not null)
        {
            var encryptedConverter = new EncryptedStringConverter(
                dataProtectionProvider,
                "Casazen.OtaIntegration.Secrets");

            modelBuilder.Entity<OtaIntegration>()
                .Property(o => o.ApiKey)
                .HasConversion(encryptedConverter);

            modelBuilder.Entity<OtaIntegration>()
                .Property(o => o.ApiSecret)
                .HasConversion(encryptedConverter);
        }

        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => t.City);
        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => new { t.City, t.IsActive, t.EffectiveFrom });

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

        // LeaseEvent → LeaseContract (cascade)
        modelBuilder.Entity<LeaseEvent>()
            .HasOne(e => e.LeaseContract)
            .WithMany(l => l.Events)
            .HasForeignKey(e => e.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<LeaseEvent>()
            .HasIndex(e => new { e.LeaseContractId, e.OccurredAt });

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
        modelBuilder.Entity<User>()
            .HasOne(u => u.Org).WithMany().HasForeignKey(u => u.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // Global tenant query filter (AC7): every read of a tenant-scoped table is scoped
        // to the caller's OrgId. Fail-closed when the caller has no org; disabled for
        // anonymous/system contexts (background jobs, design-time, unit tests).
        modelBuilder.Entity<Property>().HasQueryFilter(p => !_tenant.FilterEnabled || p.OrgId == _tenant.OrgId);
        modelBuilder.Entity<Booking>().HasQueryFilter(b => !_tenant.FilterEnabled || b.OrgId == _tenant.OrgId);
        modelBuilder.Entity<LeaseContract>().HasQueryFilter(l => !_tenant.FilterEnabled || l.OrgId == _tenant.OrgId);
        modelBuilder.Entity<Payment>().HasQueryFilter(p => !_tenant.FilterEnabled || p.OrgId == _tenant.OrgId);
        modelBuilder.Entity<RentSchedule>().HasQueryFilter(s => !_tenant.FilterEnabled || s.OrgId == _tenant.OrgId);
        modelBuilder.Entity<RentLedgerEntry>().HasQueryFilter(e => !_tenant.FilterEnabled || e.OrgId == _tenant.OrgId);

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

        // ─── Supplier console (US-022 / #292) ────────────────────────────────────
        modelBuilder.Entity<SupplierProfile>()
            .HasOne(sp => sp.Org)
            .WithOne()
            .HasForeignKey<SupplierProfile>(sp => sp.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<SupplierProfile>()
            .HasIndex(sp => sp.Status);

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
    }
}
