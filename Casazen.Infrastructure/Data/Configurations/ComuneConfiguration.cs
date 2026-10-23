using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ComuneConfiguration : IEntityTypeConfiguration<Comune>
{
    public void Configure(EntityTypeBuilder<Comune> entity)
    {
        entity.HasIndex(c => c.CadastralCode)
            .IsUnique()
            .HasFilter("\"IsActive\"")
            .HasDatabaseName("IX_Comuni_CadastralCode_Active");
        entity.HasIndex(c => c.CadastralCode);
        entity.HasIndex(c => c.NormalizedName);
        entity.HasIndex(c => c.ProvinceCode);
        entity.HasIndex(c => c.RegionIstatCode);
        entity.HasOne(c => c.SourceImport)
            .WithMany()
            .HasForeignKey(c => c.SourceImportId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(c => c.SourceImportId);
    }
}
