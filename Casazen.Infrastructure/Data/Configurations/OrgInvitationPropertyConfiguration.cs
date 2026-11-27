using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgInvitationPropertyConfiguration : IEntityTypeConfiguration<OrgInvitationProperty>
{
    public void Configure(EntityTypeBuilder<OrgInvitationProperty> entity)
    {
        entity.HasOne<OrgInvitation>()
            .WithMany()
            .HasForeignKey(p => p.InvitationId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(p => p.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        // A property is carried once by an invitation: the second one of the same pair loses the race on this index (23505).
        // It also serves the read of the properties of one invitation (and the tenant filter on top of it).
        entity.HasIndex(p => new { p.InvitationId, p.PropertyId })
            .IsUnique()
            .HasDatabaseName("UIX_OrgInvitationProperties_InvitationId_PropertyId");

        entity.HasIndex(p => p.PropertyId)
            .HasDatabaseName("IX_OrgInvitationProperties_PropertyId");
    }
}
