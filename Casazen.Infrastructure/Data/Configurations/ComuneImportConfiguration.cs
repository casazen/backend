using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ComuneImportConfiguration : IEntityTypeConfiguration<ComuneImport>
{
    public void Configure(EntityTypeBuilder<ComuneImport> entity)
    {
        entity.HasIndex(i => i.ImportedAt);
        entity.HasIndex(i => i.ReferenceDate);
    }
}
