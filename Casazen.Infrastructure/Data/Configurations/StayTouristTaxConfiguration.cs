using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The confirmed tourist tax of a stay (ADR-037-R4): one record per booking (unique index, a second confirmation replaces it), going with the
/// booking (cascade) and carrying its org (restrict like every other <c>OrgId</c> foreign key). The channel rule that decided the collector is
/// optional and a removed rule leaves the record without it (set null). The lines go with their record, one per position.
/// </summary>
internal sealed class StayTouristTaxConfiguration : IEntityTypeConfiguration<StayTouristTax>
{
    public void Configure(EntityTypeBuilder<StayTouristTax> entity)
    {
        entity.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(t => t.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(t => t.BookingId)
            .IsUnique()
            .HasDatabaseName(StayTouristTax.BookingIndexName);
        entity.HasOne<Org>().WithMany().HasForeignKey(t => t.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(t => t.OrgId);
        entity.HasOne<TouristTaxChannelRule>()
            .WithMany()
            .HasForeignKey(t => t.ChannelRuleId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
        entity.Property(t => t.CalculationHash).IsFixedLength();
        entity.HasMany(t => t.Guests)
            .WithOne()
            .HasForeignKey(g => g.StayTouristTaxId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StayTouristTaxes_AmountDueWithinTotal", "\"AmountDue\" >= 0 AND \"AmountDue\" <= \"Total\"");

            // A confirmed calculation always carries its hash and its author.
            t.HasCheckConstraint(
                "CK_StayTouristTaxes_ConfirmedHasHash",
                "\"ConfirmedAt\" IS NULL OR (\"CalculationHash\" IS NOT NULL AND \"ConfirmedByUserId\" IS NOT NULL)");
        });
    }
}

/// <summary>A line of a stay tourist tax: one per position of the record.</summary>
internal sealed class StayTouristTaxGuestConfiguration : IEntityTypeConfiguration<StayTouristTaxGuest>
{
    public void Configure(EntityTypeBuilder<StayTouristTaxGuest> entity)
    {
        entity.HasIndex(g => new { g.StayTouristTaxId, g.Position })
            .IsUnique()
            .HasDatabaseName(StayTouristTaxGuest.PositionIndexName);
    }
}
