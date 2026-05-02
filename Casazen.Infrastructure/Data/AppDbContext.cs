using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Property = Casazen.Core.Entities.Property;

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
            .HasFilter("[IsActive] = 1");

        modelBuilder.Entity<Booking>().HasIndex(b => b.PropertyId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.GuestId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.CheckInDate);
        modelBuilder.Entity<Booking>().HasIndex(b => b.Status);
        modelBuilder.Entity<Payment>().HasIndex(p => p.BookingId);
        modelBuilder.Entity<OtaIntegration>().HasIndex(o => o.PropertyId);
        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => t.City);
        modelBuilder.Entity<TouristTaxRate>().HasIndex(t => new { t.City, t.IsActive, t.EffectiveFrom });
        modelBuilder.Entity<Guest>().HasIndex(g => g.Email);
    }
}
