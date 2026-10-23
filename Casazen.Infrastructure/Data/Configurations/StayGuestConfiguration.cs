using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class StayGuestConfiguration : IEntityTypeConfiguration<StayGuest>
{
    public void Configure(EntityTypeBuilder<StayGuest> entity)
    {
        entity.HasOne(s => s.Booking)
            .WithMany(b => b.StayGuests)
            .HasForeignKey(s => s.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(s => s.Guest)
            .WithMany()
            .HasForeignKey(s => s.GuestId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => s.OrgId);
        entity.HasIndex(s => s.GuestId);
    }
}
