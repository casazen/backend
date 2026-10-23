using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ComuneOfficialProfileVersionConfiguration(bool isNpgsql) : IEntityTypeConfiguration<ComuneOfficialProfileVersion>
{
    public void Configure(EntityTypeBuilder<ComuneOfficialProfileVersion> entity)
    {
        entity.Property(v => v.IstatCode).HasMaxLength(6).IsRequired();
        if (isNpgsql)
            entity.Property(v => v.ExtractJson).HasColumnType("jsonb");
        entity.HasIndex(v => new { v.IstatCode, v.VersionNumber }).IsUnique();
        if (isNpgsql)
        {
            entity.HasIndex(v => v.IstatCode)
                .IsUnique()
                .HasFilter("\"IsCurrent\"")
                .HasDatabaseName("IX_ComuneOfficialProfileVersions_IstatCode_Current");
        }
        else
        {
            entity.HasIndex(v => v.IstatCode);
        }
    }
}
