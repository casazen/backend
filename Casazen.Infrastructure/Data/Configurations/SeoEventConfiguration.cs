using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SeoEventConfiguration : IEntityTypeConfiguration<SeoEvent>
{
    public void Configure(EntityTypeBuilder<SeoEvent> entity)
    {
        entity.HasIndex(e => e.OccurredAt)
            .HasDatabaseName("IX_SeoEvents_OccurredAt");

        entity.HasIndex(e => new { e.ComuneCode, e.OccurredAt })
            .HasDatabaseName("IX_SeoEvents_ComuneCode_OccurredAt");
    }
}
