using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgSiteDocumentConfiguration : IEntityTypeConfiguration<OrgSiteDocument>
{
    public void Configure(EntityTypeBuilder<OrgSiteDocument> entity)
    {
        entity.HasOne(d => d.Org)
            .WithMany()
            .HasForeignKey(d => d.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
        entity.Property(d => d.Source).HasConversion<string>().HasMaxLength(20);

        entity.HasIndex(d => new { d.OrgId, d.Kind, d.Version })
            .IsUnique()
            .HasDatabaseName("UIX_OrgSiteDocuments_Org_Kind_Version");
    }
}
