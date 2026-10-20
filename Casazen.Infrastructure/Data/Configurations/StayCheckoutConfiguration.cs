using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class StayCheckoutConfiguration : IEntityTypeConfiguration<StayCheckout>
{
    public void Configure(EntityTypeBuilder<StayCheckout> entity)
    {
        entity.HasOne(c => c.Booking)
            .WithMany()
            .HasForeignKey(c => c.BookingId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(c => c.CleaningRequest)
            .WithMany()
            .HasForeignKey(c => c.CleaningRequestId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
