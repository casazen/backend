using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OfficialSourceFetchConfiguration : IEntityTypeConfiguration<OfficialSourceFetch>
{
    public void Configure(EntityTypeBuilder<OfficialSourceFetch> entity)
    {
        entity.HasIndex(f => new { f.Dataset, f.RetrievedAt });
        entity.HasIndex(f => f.IstatCode);
    }
}
