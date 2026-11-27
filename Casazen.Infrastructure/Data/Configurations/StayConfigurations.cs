using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> entity)
    {
        entity.HasMany(b => b.Payments)
            .WithOne(p => p.Booking)
            .HasForeignKey(p => p.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    internal static void ConfigureLeMiePrenotazioni(EntityTypeBuilder<Booking> entity)
    {
        // "Le mie prenotazioni" finds a booking by the org of the site and its code (BK-11).
        entity.HasIndex(b => new { b.OrgId, b.BookingCode })
            .IsUnique();

        // A reservation of Octorate is one booking in an org (E02/F02/US01/T01, ADR-033-R1): the key of the import, whoever imports it and
        // however many times. Only the rows that have an Octorate id are in it; the others (null) are as many as there are bookings.
        entity.HasIndex(b => new { b.OrgId, b.OctorateReservationId })
            .IsUnique()
            .HasFilter("\"OctorateReservationId\" IS NOT NULL")
            .HasDatabaseName(Booking.OctorateReservationIndexName);
    }

    internal static void ConfigurePropertyIdIndex(EntityTypeBuilder<Booking> entity)
    {
        entity.HasIndex(b => b.PropertyId);
        entity.HasIndex(b => b.GuestId);
        entity.HasIndex(b => b.CheckInDate);
        entity.HasIndex(b => b.Status);
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<Booking> entity)
    {
        entity.HasIndex(b => b.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<Booking> entity)
    {
        entity.HasOne(b => b.Org).WithMany().HasForeignKey(b => b.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    internal static void ConfigureICalFeedIdIndex(EntityTypeBuilder<Booking> entity)
    {
        // The stays of a feed, found again by the sync (feed + block UID) when a block comes back.
        entity.HasIndex(b => new { b.ICalFeedId, b.ExternalId });
    }
}

internal sealed class GuestConfiguration : IEntityTypeConfiguration<Guest>
{
    public void Configure(EntityTypeBuilder<Guest> entity)
    {
        entity.HasMany(g => g.Bookings)
            .WithOne(b => b.Guest)
            .HasForeignKey(b => b.GuestId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    internal static void ConfigureEmailIndex(EntityTypeBuilder<Guest> entity)
    {
        entity.HasIndex(g => g.Email);
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<Guest> entity)
    {
        entity.HasIndex(g => g.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<Guest> entity)
    {
        // TN-1: a guest belongs to exactly one org. No unique (OrgId, lower(Email)) index: host and
        // direct bookings store one guest snapshot per booking (#431), so one org legitimately holds
        // several rows with the same e-mail.
        entity.HasOne(g => g.Org).WithMany().HasForeignKey(g => g.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GuestStayChangeConfiguration : IEntityTypeConfiguration<GuestStayChange>
{
    public void Configure(EntityTypeBuilder<GuestStayChange> entity)
    {
        entity.HasOne<Booking>().WithMany().HasForeignKey(change => change.BookingId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(change => change.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(change => change.BookingId).IsUnique().HasFilter("\"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL")
            .HasDatabaseName("UIX_GuestStayChanges_Pending");
        entity.Property(change => change.Currency).HasMaxLength(3);
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> entity)
    {
        entity.HasIndex(p => p.BookingId);
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<Payment> entity)
    {
        entity.HasIndex(p => p.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<Payment> entity)
    {
        entity.HasOne(p => p.Org).WithMany().HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    internal static void ConfigureOtaWithholdingTax(EntityTypeBuilder<Payment> entity)
    {
        entity.Property(p => p.OtaWithholdingTax)
            .HasPrecision(18, 2);
        entity.Property(p => p.NetAmountAfterWithholding)
            .HasPrecision(18, 2);
    }

    internal static void ConfigureOrgIdIndex2(EntityTypeBuilder<Payment> entity)
    {
        // A payment is recorded on its Octorate reservation once (E08/F01/US01/T01, AC-NF74-4): its transaction is unique in the org. Only
        // the payments recorded there are in the index.
        entity.HasIndex(p => new { p.OrgId, p.OctorateTransactionId })
            .IsUnique()
            .HasFilter("\"OctorateTransactionId\" IS NOT NULL")
            .HasDatabaseName(Payment.OctorateTransactionIndexName);
    }
}

internal sealed class OtaIntegrationConfiguration : IEntityTypeConfiguration<OtaIntegration>
{
    public void Configure(EntityTypeBuilder<OtaIntegration> entity)
    {
        entity.HasIndex(o => o.PropertyId);
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<OtaIntegration> entity)
    {
        entity.HasIndex(o => o.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<OtaIntegration> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(o => o.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
