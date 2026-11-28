using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

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

        entity.HasIndex(t => t.StripePaymentIntentId)
            .IsUnique()
            .HasFilter("\"StripePaymentIntentId\" IS NOT NULL")
            .HasDatabaseName(StayTouristTax.PaymentIntentIndexName);
        entity.HasMany(t => t.Guests)
            .WithOne()
            .HasForeignKey(g => g.StayTouristTaxId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StayTouristTaxes_AmountDueWithinTotal", "\"AmountDue\" >= 0 AND \"AmountDue\" <= \"Total\"");

            t.HasCheckConstraint(
                "CK_StayTouristTaxes_ConfirmedHasHash",
                "\"ConfirmedAt\" IS NULL OR (\"CalculationHash\" IS NOT NULL AND \"ConfirmedByUserId\" IS NOT NULL)");

            t.HasCheckConstraint(
                "CK_StayTouristTaxes_RefundedWithinCollected",
                "\"CollectedAmount\" >= 0 AND \"RefundedAmount\" >= 0 AND \"RefundedAmount\" <= \"CollectedAmount\"");
        });
    }
}

internal sealed class StayTouristTaxGuestConfiguration : IEntityTypeConfiguration<StayTouristTaxGuest>
{
    public void Configure(EntityTypeBuilder<StayTouristTaxGuest> entity)
    {
        entity.HasIndex(g => new { g.StayTouristTaxId, g.Position })
            .IsUnique()
            .HasDatabaseName(StayTouristTaxGuest.PositionIndexName);
    }
}
