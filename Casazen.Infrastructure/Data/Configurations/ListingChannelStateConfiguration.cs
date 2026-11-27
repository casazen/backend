using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ListingChannelStateConfiguration : IEntityTypeConfiguration<ListingChannelState>
{
    public void Configure(EntityTypeBuilder<ListingChannelState> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.PropertyId, s.ChannelCode })
            .IsUnique()
            .HasDatabaseName("UIX_ListingChannelStates_PropertyId_ChannelCode");
        entity.HasIndex(s => s.OrgId);
        entity.ToTable(t => t.HasCheckConstraint("CK_ListingChannelStates_ContentVersion", "\"ContentVersion\" >= 0"));
    }
}

internal sealed class PhotoUploadTaskConfiguration : IEntityTypeConfiguration<PhotoUploadTask>
{
    public void Configure(EntityTypeBuilder<PhotoUploadTask> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(t => t.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(t => t.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(t => new { t.PropertyId, t.PhotoUrl })
            .IsUnique()
            .HasDatabaseName("UIX_PhotoUploadTasks_PropertyId_PhotoUrl");
        entity.HasIndex(t => t.OrgId);
    }
}
