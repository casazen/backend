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
    public DbSet<TaxRate> TaxRates { get; set; } = null!;
    public DbSet<AlloggiatiWebReport> AlloggiatiWebReports { get; set; } = null!;

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

        // Indexes
        modelBuilder.Entity<Property>().HasIndex(p => p.OwnerId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.PropertyId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.GuestId);
        modelBuilder.Entity<Booking>().HasIndex(b => b.CheckInDate);
        modelBuilder.Entity<Booking>().HasIndex(b => b.Status);
        modelBuilder.Entity<Payment>().HasIndex(p => p.BookingId);
        modelBuilder.Entity<OtaIntegration>().HasIndex(o => o.PropertyId);
        modelBuilder.Entity<TaxRate>().HasIndex(t => new { t.City, t.EffectiveFrom });
        modelBuilder.Entity<AlloggiatiWebReport>().HasIndex(r => r.BookingId);
        modelBuilder.Entity<AlloggiatiWebReport>().HasIndex(r => r.Status);
    }
}
