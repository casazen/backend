using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The evidence kept for the reviewer of a self-hosted verification (E70/F01/US01/T02, NF-195, ADR-013-R20): the row carries its org (TN-2, restrict like every
/// other <c>OrgId</c> foreign key) and on purpose no other foreign key (see <see cref="IdentityReviewEvidence"/>). Two indexes: the expiry of the rows not
/// deleted yet, for the purge, and (org, booking), for the reads of a booking, its erasure and the end of its verification.
/// </summary>
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
