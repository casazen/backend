using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The proposed merges of two CRM profiles (NF-88): a row follows its two profiles (cascade) and carries their org (TN-2, restrict like every
/// other <c>OrgId</c> foreign key). The unique index on the org and the two profiles keeps one row for each pair, as long as the service stores
/// the smaller id first; the database cannot check the order, because PostgreSQL sorts a <c>uuid</c> differently from <see cref="Guid.CompareTo(Guid)"/>.
/// </summary>
internal sealed class GuestMergeCandidateConfiguration : IEntityTypeConfiguration<GuestMergeCandidate>
{
    public void Configure(EntityTypeBuilder<GuestMergeCandidate> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<GuestCrmProfile>().WithMany().HasForeignKey(c => c.ProfileAId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<GuestCrmProfile>().WithMany().HasForeignKey(c => c.ProfileBId).OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(c => new { c.OrgId, c.ProfileAId, c.ProfileBId })
            .IsUnique()
            .HasDatabaseName("UIX_GuestMergeCandidates_OrgId_ProfileAId_ProfileBId");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_GuestMergeCandidates_Score",
                $"\"Score\" >= {GuestMergeCandidate.MinScore} AND \"Score\" <= {GuestMergeCandidate.MaxScore}");
            t.HasCheckConstraint("CK_GuestMergeCandidates_DistinctProfiles", "\"ProfileAId\" <> \"ProfileBId\"");
        });
    }
}
