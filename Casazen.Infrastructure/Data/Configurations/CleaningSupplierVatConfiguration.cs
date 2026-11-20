using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The answer of the agency about the VAT of the company of a cleaning request (FIX/BE-CLEANING-VAT): the columns of <c>ServiceRequests</c>, and the check that keeps the
/// rate with «Sì» and only with it.
/// </summary>
internal sealed class ServiceRequestSupplierVatConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        entity.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceRequests_SupplierVat",
            $"(\"SupplierVat\" = {(int)CleaningSupplierVat.Applied} AND \"SupplierVatRatePercent\" IS NOT NULL) "
            + $"OR (\"SupplierVat\" IS DISTINCT FROM {(int)CleaningSupplierVat.Applied} AND \"SupplierVatRatePercent\" IS NULL)"));
    }
}

/// <summary>
/// What an organization remembers of the VAT of a cleaning company (FIX/BE-CLEANING-VAT): one row for each organization and company, gone with either of them.
/// </summary>
internal sealed class SupplierVatDefaultConfiguration : IEntityTypeConfiguration<SupplierVatDefault>
{
    public void Configure(EntityTypeBuilder<SupplierVatDefault> entity)
    {
        entity.HasIndex(d => new { d.OrgId, d.SupplierOrgId }, SupplierVatDefault.OrgSupplierIndexName).IsUnique();

        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(d => d.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(d => d.SupplierOrgId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
