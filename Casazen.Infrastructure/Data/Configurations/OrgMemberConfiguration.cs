using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgMemberConfiguration : IEntityTypeConfiguration<OrgMember>
{
    public void Configure(EntityTypeBuilder<OrgMember> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One org per user. It is also what two parallel onboardings of the same user race on: the loser gets 23505.
        entity.HasIndex(m => m.UserId)
            .IsUnique()
            .HasDatabaseName("UIX_OrgMembers_UserId");

        // The tenant filter reads by OrgId; the team page lists an org's people and finds its owner.
        entity.HasIndex(m => new { m.OrgId, m.Role })
            .HasDatabaseName("IX_OrgMembers_OrgId_Role");
    }
}
