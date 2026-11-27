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

        entity.HasIndex(s => new { s.BookingId, s.Status })
            .IsUnique()
            .HasFilter("\"Status\" IN (0, 1, 2, 3)")
            .HasDatabaseName("UIX_GuestCheckInSessions_BookingId_ActiveStatus");

        entity.Property(s => s.Language).HasMaxLength(2);

        entity.Property(s => s.BiometricConsentVersion).HasMaxLength(50);

        entity.Property(s => s.BiometricConsentTextHash).HasMaxLength(64);
        entity.Property(s => s.DocumentCheckJson).HasColumnType("jsonb");

        // E70/F01/US01/T02 (NF-188, NF-196): the guided retries of the self-hosted verification start at 0, on every row written before the column too; the counters
        // read the sessions an org opened in a period by the time of the opening.
        entity.Property(s => s.IdentityGuidedRetries).HasDefaultValue(0);
        entity.HasIndex(s => new { s.OrgId, s.IdentityOpenedAt });
    }
}
