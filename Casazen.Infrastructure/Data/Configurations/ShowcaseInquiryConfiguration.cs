using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ShowcaseInquiryConfiguration : IEntityTypeConfiguration<ShowcaseInquiry>
{
    public void Configure(EntityTypeBuilder<ShowcaseInquiry> entity)
    {
        entity.HasOne(i => i.SupplierProfile)
            .WithMany()
            .HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // The request born from the inquiry. Set to null if the request is ever removed for good: the inquiry only remembers it.
        entity.HasOne<ServiceRequest>()
            .WithMany()
            .HasForeignKey(i => i.ServiceRequestId)
            .OnDelete(DeleteBehavior.SetNull);

        // The column says what it holds: encrypted at rest through the value converter of EncryptedColumns.
        entity.Property(i => i.Payload).HasColumnName("PayloadEncrypted");

        // The same client request id is the same inquiry (idempotency).
        entity.HasIndex(i => new { i.OrgId, i.ClientRequestId })
            .IsUnique()
            .HasDatabaseName("UIX_ShowcaseInquiries_OrgId_ClientRequestId");

        // The upkeep job reads the expired ones of every supplier; the cap of the requests that wait counts the ones of one address.
        entity.HasIndex(i => i.ExpiresAt).HasDatabaseName("IX_ShowcaseInquiries_ExpiresAt");
        entity.HasIndex(i => new { i.OrgId, i.EmailHash }).HasDatabaseName("IX_ShowcaseInquiries_OrgId_EmailHash");

        entity.ToTable(t => t.HasCheckConstraint("CK_ShowcaseInquiries_Expiry", "\"ExpiresAt\" > \"CreatedAt\""));
    }
}
