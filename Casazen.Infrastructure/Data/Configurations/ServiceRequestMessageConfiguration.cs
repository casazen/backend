using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceRequestMessageConfiguration : IEntityTypeConfiguration<ServiceRequestMessage>
{
    public void Configure(EntityTypeBuilder<ServiceRequestMessage> entity)
    {
        // The messages go with their request.
        entity.HasOne(m => m.ServiceRequest)
            .WithMany()
            .HasForeignKey(m => m.ServiceRequestId)
            .OnDelete(DeleteBehavior.Cascade);

        // The column says what it holds: encrypted at rest through the value converter of EncryptedColumns.
        entity.Property(m => m.Body).HasColumnName("BodyEncrypted");

        // The thread of one request, in order, scoped by the supplier org (the table is not tenant-filtered).
        entity.HasIndex(m => new { m.OrgId, m.ServiceRequestId, m.CreatedAt })
            .HasDatabaseName("IX_ServiceRequestMessages_OrgId_ServiceRequestId_CreatedAt");

        entity.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceRequestMessages_Author",
            $"\"Author\" IN ({(int)ServiceRequestMessageAuthor.Customer}, {(int)ServiceRequestMessageAuthor.Supplier}, {(int)ServiceRequestMessageAuthor.System})"));
    }
}
