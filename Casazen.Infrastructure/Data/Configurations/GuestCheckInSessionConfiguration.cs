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

        // NF-40: the language the guest chose (two letters) and the one reminder of the missing data (E03/F01/US01/T02).
        entity.Property(s => s.Language).HasMaxLength(2);

        // E03/F04/US02/T01: the biometric consent (its version), and the checks of the document (flags and a hash, no value read on it).
        entity.Property(s => s.BiometricConsentVersion).HasMaxLength(50);
        entity.Property(s => s.DocumentCheckJson).HasColumnType("jsonb");
    }
}
