using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> entity)
    {
        entity.HasOne(r => r.Property)
            .WithMany()
            .HasForeignKey(r => r.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(r => r.Booking)
            .WithMany()
            .HasForeignKey(r => r.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        // Two parallel submissions for one booking (the link opened twice): the loser gets 23505 on UIX_Reviews_BookingId and 409.
        entity.ToTable(t => t.HasCheckConstraint("CK_Reviews_Rating", "\"Rating\" BETWEEN 1 AND 5"));
    }
}
