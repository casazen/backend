using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceCustomerConfiguration : IEntityTypeConfiguration<ServiceCustomer>
{
    public void Configure(EntityTypeBuilder<ServiceCustomer> entity)
    {
        // A child of the supplier profile like the agenda: the repair moves the customers before it deletes a profile.
        entity.HasOne(c => c.SupplierProfile)
            .WithMany()
            .HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        // One customer per supplier and address; the HMAC of the address is the only thing the address is found by.
        entity.HasIndex(c => new { c.OrgId, c.EmailHash })
            .IsUnique()
            .HasDatabaseName("UIX_ServiceCustomers_OrgId_EmailHash");
    }
}
