using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Property = Casazen.Core.Entities.Property;
using AppContextEntity = Casazen.Core.Entities.AppContext;

namespace Casazen.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Property> Properties { get; set; } = null!;
    public DbSet<Booking> Bookings { get; set; } = null!;
    public DbSet<Guest> Guests { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<OtaIntegration> OtaIntegrations { get; set; } = null!;
    public DbSet<TouristTaxRate> TouristTaxRates { get; set; } = null!;
    public DbSet<OtaSyncLog> OtaSyncLogs { get; set; } = null!;
    public DbSet<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = null!;
    public DbSet<TaxRate> TaxRates { get; set; } = null!;
    public DbSet<CancellationPolicy> CancellationPolicies { get; set; } = null!;
    public DbSet<PricingAdapterConfig> PricingAdapterConfigs { get; set; } = null!;
    public DbSet<PricingHistory> PricingHistories { get; set; } = null!;
    public DbSet<PropertyDocument> PropertyDocuments { get; set; } = null!;

    // Long-term lease
    public DbSet<LeaseContract> LeaseContracts { get; set; } = null!;
    public DbSet<Party> Parties { get; set; } = null!;
    public DbSet<LeaseRegistration> LeaseRegistrations { get; set; } = null!;
    public DbSet<LeaseEvent> LeaseEvents { get; set; } = null!;
    public DbSet<AppContextEntity> AppContexts { get; set; } = null!;
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
        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => t.City);
        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => new { t.City, t.IsActive, t.EffectiveFrom });
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
            new RolePermission { RoleId = 3, PermissionKey = "admin.stats.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.users.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.users.manage" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.cin.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.jobs.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.tax.manage" });
    }
}
