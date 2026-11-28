using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestContactConfiguration : IEntityTypeConfiguration<GuestContact>
{
    public void Configure(EntityTypeBuilder<GuestContact> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Restrict);

        entity.HasOne<GuestCrmProfile>().WithMany().HasForeignKey(c => c.GuestCrmProfileId).OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Guest>().WithMany().HasForeignKey(c => c.GuestId).OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(c => new { c.OrgId, c.Channel, c.ValueHash })
            .IsUnique()
            .HasDatabaseName("UIX_GuestContacts_OrgId_Channel_ValueHash");
        entity.HasIndex(c => new { c.OrgId, c.GuestCrmProfileId })
            .HasDatabaseName("IX_GuestContacts_OrgId_GuestCrmProfileId");
        entity.HasIndex(c => new { c.OrgId, c.IsSuppressed })
            .HasDatabaseName("IX_GuestContacts_OrgId_IsSuppressed");
    }
}
