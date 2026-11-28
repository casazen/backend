using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ShowcaseBookingHoldConfiguration : IEntityTypeConfiguration<ShowcaseBookingHold>
{
    public void Configure(EntityTypeBuilder<ShowcaseBookingHold> entity)
    {
        entity.HasOne(h => h.SupplierProfile)
            .WithMany()
            .HasForeignKey(h => h.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<ServiceRequest>()
            .WithMany()
            .HasForeignKey(h => h.ServiceRequestId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.Property(h => h.Payload).HasColumnName("PayloadEncrypted");

        // The same client request id is the same hold (idempotency); a code is unique among the supplier's holds.
        entity.HasIndex(h => new { h.OrgId, h.ClientRequestId })
            .IsUnique()
            .HasDatabaseName("UIX_ShowcaseBookingHolds_OrgId_ClientRequestId");
        entity.HasIndex(h => new { h.OrgId, h.PublicCode })
            .IsUnique()
            .HasDatabaseName("UIX_ShowcaseBookingHolds_OrgId_PublicCode");

        entity.HasIndex(h => new { h.OrgId, h.StartUtc }).HasDatabaseName("IX_ShowcaseBookingHolds_OrgId_StartUtc");
        entity.HasIndex(h => h.ExpiresAt).HasDatabaseName("IX_ShowcaseBookingHolds_ExpiresAt");
        entity.HasIndex(h => new { h.OrgId, h.EmailHash }).HasDatabaseName("IX_ShowcaseBookingHolds_OrgId_EmailHash");

        entity.HasIndex(h => h.StripePaymentIntentId)
            .IsUnique()
            .HasDatabaseName("UIX_ShowcaseBookingHolds_StripePaymentIntentId")
            .HasFilter("\"StripePaymentIntentId\" IS NOT NULL");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ShowcaseBookingHolds_Interval", "\"StartUtc\" < \"EndUtc\"");
            t.HasCheckConstraint("CK_ShowcaseBookingHolds_Expiry", "\"ExpiresAt\" > \"CreatedAt\"");

            t.HasCheckConstraint(
                "CK_ShowcaseBookingHolds_Payment",
                "(\"StripePaymentIntentId\" IS NULL AND \"PaymentAmountCents\" IS NULL AND \"PaymentAuthorizedAt\" IS NULL) "
                + "OR (\"StripePaymentIntentId\" IS NOT NULL AND \"PaymentAmountCents\" > 0)");
        });
    }
}
