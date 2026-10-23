using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ReviewInviteConfiguration : IEntityTypeConfiguration<ReviewInvite>
{
    public void Configure(EntityTypeBuilder<ReviewInvite> entity)
    {
        entity.HasOne(i => i.Booking)
            .WithMany()
            .HasForeignKey(i => i.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
