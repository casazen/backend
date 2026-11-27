using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The welcome QR link of a property (NF-90): rows follow their property (cascade) and carry its org (TN-2, restrict like every other
/// <c>OrgId</c> foreign key). The token is unique in the whole table, revoked links included, so a printed code never reaches another
/// property; a property has one active link: the partial unique index on the property where the link is not revoked.
/// </summary>
internal sealed class PropertyWelcomeLinkConfiguration : IEntityTypeConfiguration<PropertyWelcomeLink>
{
    public void Configure(EntityTypeBuilder<PropertyWelcomeLink> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(l => l.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Property>().WithMany().HasForeignKey(l => l.PropertyId).OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(l => l.Token)
            .IsUnique()
            .HasDatabaseName("UIX_PropertyWelcomeLinks_Token");
        entity.HasIndex(l => l.PropertyId)
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName("UIX_PropertyWelcomeLinks_PropertyId_Active");
        entity.HasIndex(l => l.OrgId);
    }
}
