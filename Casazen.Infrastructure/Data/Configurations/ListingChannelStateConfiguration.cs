using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The state of a property on one channel of the channel manager (NF-27, NF-38, ADR-034): rows follow their property (cascade) and carry its
/// org (TN-2, restrict like every other <c>OrgId</c> foreign key). A property has one row for each channel: the unique index is what lets two
/// publications of the same property end on one row, whichever of them writes first.
/// </summary>
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

/// <summary>
/// The sending of one photo of a property to the channel manager (NF-32, ADR-036-R3): same ownership as the state of a channel. A photo is its
/// address, so the unique index on the property and the address is what keeps one photo from being sent twice.
/// </summary>
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
