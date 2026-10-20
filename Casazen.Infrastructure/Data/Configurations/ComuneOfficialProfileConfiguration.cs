using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ComuneOfficialProfileConfiguration : IEntityTypeConfiguration<ComuneOfficialProfile>
{
    public void Configure(EntityTypeBuilder<ComuneOfficialProfile> entity)
    {
        entity.Property(p => p.IstatCode).HasMaxLength(6).IsRequired();
        entity.HasMany(p => p.Versions)
            .WithOne()
            .HasForeignKey(v => v.IstatCode)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
