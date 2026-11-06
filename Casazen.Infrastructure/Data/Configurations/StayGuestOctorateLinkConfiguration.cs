using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The guests of a stay as sent to Octorate (E03/F01/US01/T03): one row per booking and position (unique), going with the booking (cascade) and
/// carrying its org (restrict like every other <c>OrgId</c> foreign key). The retry job reads the rows to send again by status.
/// </summary>
internal sealed class StayGuestOctorateLinkConfiguration : IEntityTypeConfiguration<StayGuestOctorateLink>
{
    public void Configure(EntityTypeBuilder<StayGuestOctorateLink> entity)
    {
        entity.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(l => l.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(l => new { l.BookingId, l.Position })
            .IsUnique()
            .HasDatabaseName(StayGuestOctorateLink.PositionIndexName);
        entity.HasOne<Org>().WithMany().HasForeignKey(l => l.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(l => new { l.OrgId, l.Status });
        entity.Property(l => l.PayloadHash).IsFixedLength();
    }
}
