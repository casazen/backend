using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestCheckInSessionConfiguration : IEntityTypeConfiguration<GuestCheckInSession>
{
    public void Configure(EntityTypeBuilder<GuestCheckInSession> entity)
    {
        entity.HasOne(s => s.Booking)
            .WithMany()
            .HasForeignKey(s => s.BookingId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(s => s.TokenHash)
            .IsUnique()
            .HasDatabaseName("UIX_GuestCheckInSessions_TokenHash");

        // Only one active session per booking at a time (statuses 0-3 are active)
        entity.HasIndex(s => new { s.BookingId, s.Status })
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1, 2, 3)")
            .HasDatabaseName("UIX_GuestCheckInSessions_BookingId_ActiveStatus");
    }
}
