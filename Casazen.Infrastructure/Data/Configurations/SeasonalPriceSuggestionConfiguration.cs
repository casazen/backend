using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SeasonalPriceSuggestionConfiguration : IEntityTypeConfiguration<SeasonalPriceSuggestion>
{
    public void Configure(EntityTypeBuilder<SeasonalPriceSuggestion> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.PropertyId, s.StayDate }).IsUnique();
        entity.HasIndex(s => s.OrgId);
        entity.Property(s => s.Rule).HasConversion<string>();
        entity.Property(s => s.Holiday).HasConversion<string>();
    }
}
