using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
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

        // NF-40: the residence (same shape as the birth place), the statistical fields (closed lists of IstatGuestFieldLists). Every new column
        // is nullable. TaxExemptionCode has no length: it is encrypted with the document (EncryptedColumns), so the column is text.
        entity.Property(s => s.ResidenceComuneCode).HasMaxLength(9);
        entity.Property(s => s.ResidenceComuneName).HasMaxLength(100);
        entity.Property(s => s.ResidenceProvince).HasMaxLength(2);
        entity.Property(s => s.ResidenceCountryCode).HasMaxLength(9);
        entity.Property(s => s.ResidenceCountryName).HasMaxLength(100);
        entity.Property(s => s.TourismType).HasMaxLength(IstatGuestFieldLists.MaxCodeLength);
        entity.Property(s => s.TravelWay).HasMaxLength(IstatGuestFieldLists.MaxCodeLength);
        entity.Property(s => s.EducationLevel).HasMaxLength(IstatGuestFieldLists.MaxCodeLength);
    }
}
