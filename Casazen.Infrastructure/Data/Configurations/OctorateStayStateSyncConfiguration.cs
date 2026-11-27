using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The states of a stay sent to Octorate (E03/F01/US01/T03, NF-45): one row per booking and state (unique: each state is sent once), going with
/// the booking (cascade) and carrying its org (restrict). The retry job reads the rows to send again by org and status.
/// </summary>
internal sealed class OctorateStayStateSyncConfiguration : IEntityTypeConfiguration<OctorateStayStateSync>
{
    public void Configure(EntityTypeBuilder<OctorateStayStateSync> entity)
    {
        entity.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(s => s.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(s => new { s.BookingId, s.State })
            .IsUnique()
            .HasDatabaseName(OctorateStayStateSync.StateIndexName);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.OrgId, s.Status });
    }
}
