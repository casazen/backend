using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ReviewReportConfiguration : IEntityTypeConfiguration<ReviewReport>
{
    public void Configure(EntityTypeBuilder<ReviewReport> entity)
    {
        entity.Property(r => r.Status).IsConcurrencyToken();
        entity.HasOne(r => r.Review)
            .WithMany()
            .HasForeignKey(r => r.ReviewId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
