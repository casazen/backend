using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class IdentityReviewEvidenceConfiguration : IEntityTypeConfiguration<IdentityReviewEvidence>
{
    public void Configure(EntityTypeBuilder<IdentityReviewEvidence> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(e => e.OrgId).OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(e => e.ExpiresAt)
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName(IdentityReviewEvidence.LiveExpiryIndexName);
        entity.HasIndex(e => new { e.OrgId, e.BookingId });
    }
}
