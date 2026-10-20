using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertyMemberAccessConfiguration : IEntityTypeConfiguration<PropertyMemberAccess>
{
    public void Configure(EntityTypeBuilder<PropertyMemberAccess> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // The grants of a property go with it (it is soft-deleted, so in practice never) and the grants of a person go
        // with the account.
        entity.HasOne(a => a.Property)
            .WithMany(p => p.MemberAccesses)
            .HasForeignKey(a => a.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A member reaches a property once: the second grant of the same pair loses the race on this index (23505).
        // It also serves the EXISTS of every list (UserId first) and the reads of the member's own grants.
        entity.HasIndex(a => new { a.UserId, a.PropertyId })
            .IsUnique()
            .HasDatabaseName("UIX_PropertyMemberAccesses_UserId_PropertyId");

        // «Chi può accedere» per property, and the tenant filter (OrgId) of every read.
        entity.HasIndex(a => new { a.PropertyId })
            .HasDatabaseName("IX_PropertyMemberAccesses_PropertyId");
        entity.HasIndex(a => new { a.OrgId, a.UserId })
            .HasDatabaseName("IX_PropertyMemberAccesses_OrgId_UserId");
    }
}
