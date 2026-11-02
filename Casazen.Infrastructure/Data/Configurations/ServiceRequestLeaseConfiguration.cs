using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>The column of <c>ServiceRequests</c> that ties a long-rent request to the lease it is about (LR-03c).</summary>
internal sealed class ServiceRequestLeaseConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        // Deleting a lease (only a draft is ever deleted) keeps the requests: they lose the link and stay requests for the property.
        entity.HasOne<LeaseContract>()
            .WithMany()
            .HasForeignKey(sr => sr.LeaseContractId)
            .OnDelete(DeleteBehavior.SetNull);

        // The requests of a lease are read together, for the sheet of the contract.
        entity.HasIndex(sr => sr.LeaseContractId, "IX_ServiceRequests_LeaseContractId")
            .HasFilter("\"LeaseContractId\" IS NOT NULL");

        // Only a long-rent request is for a lease: a short-rent request is for a stay, a showcase one for a customer.
        entity.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceRequests_LeaseContract",
            $"\"LeaseContractId\" IS NULL OR \"RentalContext\" = {(int)ServiceRequestRentalContext.LongRent}"));
    }
}
