using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceCustomerConfiguration : IEntityTypeConfiguration<ServiceCustomer>
{
    public void Configure(EntityTypeBuilder<ServiceCustomer> entity)
    {
        entity.HasOne(c => c.SupplierProfile)
            .WithMany()
            .HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(c => new { c.OrgId, c.EmailHash })
            .IsUnique()
            .HasDatabaseName("UIX_ServiceCustomers_OrgId_EmailHash");
    }
}
