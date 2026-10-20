using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceRequestShowcaseConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        entity.HasOne(sr => sr.Customer)
            .WithMany()
            .HasForeignKey(sr => sr.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(sr => sr.CustomerId).HasDatabaseName("IX_ServiceRequests_CustomerId");

        // The code is unique for the supplier among its showcase requests (the host requests have none).
        entity.HasIndex(sr => new { sr.SupplierOrgId, sr.PublicCode })
            .IsUnique()
            .HasFilter("\"PublicCode\" IS NOT NULL")
            .HasDatabaseName("UIX_ServiceRequests_SupplierOrgId_PublicCode");

        entity.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceRequests_Context",
            $"(\"RentalContext\" = {(int)ServiceRequestRentalContext.Showcase} "
            + "AND \"PropertyId\" IS NULL AND \"BookingId\" IS NULL "
            + $"AND \"Source\" = {(int)ServiceRequestSource.Showcase} "
            + "AND \"CustomerId\" IS NOT NULL AND \"PublicCode\" IS NOT NULL AND \"LocationCity\" IS NOT NULL) OR "
            + $"(\"RentalContext\" IN ({(int)ServiceRequestRentalContext.ShortRent}, {(int)ServiceRequestRentalContext.LongRent}) "
            + "AND \"PropertyId\" IS NOT NULL "
            + $"AND \"Source\" = {(int)ServiceRequestSource.Host} "
            + "AND \"CustomerId\" IS NULL AND \"PublicCode\" IS NULL "
            + "AND \"LocationComuneIstat\" IS NULL AND \"LocationCity\" IS NULL AND \"LocationPostalCode\" IS NULL "
            + "AND \"LocationAddress\" IS NULL AND \"LocationFloor\" IS NULL AND \"LocationAccessNotes\" IS NULL)"));
    }
}
