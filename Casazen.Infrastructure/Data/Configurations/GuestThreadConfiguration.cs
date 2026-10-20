using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestThreadConfiguration : IEntityTypeConfiguration<GuestThread>
{
    public void Configure(EntityTypeBuilder<GuestThread> entity)
    {
        entity.HasOne(t => t.Property)
            .WithMany()
            .HasForeignKey(t => t.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(t => t.Booking)
            .WithMany()
            .HasForeignKey(t => t.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(t => t.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
