using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
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
    }
}
