using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceRequestLeaseConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        entity.HasOne<LeaseContract>()
            .WithMany()
            .HasForeignKey(sr => sr.LeaseContractId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(sr => sr.LeaseContractId, "IX_ServiceRequests_LeaseContractId")
            .HasFilter("\"LeaseContractId\" IS NOT NULL");

        entity.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceRequests_LeaseContract",
            $"\"LeaseContractId\" IS NULL OR \"RentalContext\" = {(int)ServiceRequestRentalContext.LongRent}"));
    }
}
