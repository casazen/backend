using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class TerritorialRentAgreementConfiguration : IEntityTypeConfiguration<TerritorialRentAgreement>
{
    public void Configure(EntityTypeBuilder<TerritorialRentAgreement> entity)
    {
        entity.HasIndex(a => a.Comune);
        entity.HasMany(a => a.Bands)
            .WithOne(b => b.Agreement)
            .HasForeignKey(b => b.TerritorialRentAgreementId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasMany(a => a.Signatories)
            .WithOne(s => s.Agreement)
            .HasForeignKey(s => s.TerritorialRentAgreementId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
