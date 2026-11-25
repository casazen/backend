using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestSupplierOfferConfiguration : IEntityTypeConfiguration<GuestSupplierOffer>
{
    public void Configure(EntityTypeBuilder<GuestSupplierOffer> builder)
    {
        builder.HasOne<Org>().WithMany().HasForeignKey(x => x.HostOrgId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<SupplierServiceListing>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Org>().WithMany().HasForeignKey(x => x.SupplierOrgId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.PropertyId, x.ServiceId }).IsUnique();
        builder.HasIndex(x => x.SupplierOrgId);
        builder.Property(x => x.Version).IsRowVersion();
    }
}

internal sealed class GuestSupplierOrderConfiguration : IEntityTypeConfiguration<GuestSupplierOrder>
{
    public void Configure(EntityTypeBuilder<GuestSupplierOrder> builder)
    {
        builder.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Org>().WithMany().HasForeignKey(x => x.HostOrgId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Org>().WithMany().HasForeignKey(x => x.SupplierOrgId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Property>().WithMany().HasForeignKey(x => x.PropertyId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.StripePaymentIntentId).IsUnique();
        builder.HasIndex(x => new { x.BookingId, x.RequestId }).IsUnique();
        builder.HasIndex(x => new { x.SupplierOrgId, x.State });
        builder.HasIndex(x => new { x.HostOrgId, x.PropertyId });
        builder.Property(x => x.Version).IsRowVersion();
    }
}
