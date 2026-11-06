using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The proposed merges of two CRM profiles (NF-88): a row follows its two profiles (cascade) and carries their org (TN-2, restrict like every
/// other <c>OrgId</c> foreign key). A pair is stored in one order only, the smaller profile id first, and the database enforces it with a check
/// (<c>"ProfileAId" &lt; "ProfileBId"</c>, which also refuses a profile paired with itself): (A, B) and (B, A) cannot both exist, so the unique
/// index on the org and the two profiles keeps one row for each pair. PostgreSQL compares a <c>uuid</c> byte by byte in its canonical order, which
/// is the order of <see cref="Guid.CompareTo(Guid)"/>: the code that inserts a pair sorts it that way, and the check accepts what it writes.
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
            // The canonical order of the pair; strictly smaller, so a profile cannot be paired with itself either.
            t.HasCheckConstraint("CK_GuestMergeCandidates_ProfileOrder", "\"ProfileAId\" < \"ProfileBId\"");
        });
    }
}
