using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The stay link of a booking (E50/F01/US01/T01, NF-152): rows follow their booking (cascade) and carry its org (TN-2, restrict like every other
/// <c>OrgId</c> foreign key). The token is unique in the whole table, revoked links included, so a link never reaches another stay; a booking has one
/// active link: the partial unique index on the booking where the link is not revoked. The account a guest added the stay to is indexed for the
/// list of the guest's stays (E50/F01/US02).
/// </summary>
internal sealed class GuestPortalLinkConfiguration : IEntityTypeConfiguration<GuestPortalLink>
{
    /// <summary>Name of the unique index of the tokens.</summary>
    public const string TokenIndexName = "UIX_GuestPortalLinks_Token";

    /// <summary>Name of the partial unique index of the one active link of a booking.</summary>
    public const string ActiveBookingIndexName = "UIX_GuestPortalLinks_BookingId_Active";

    public void Configure(EntityTypeBuilder<GuestPortalLink> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(l => l.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Booking>().WithMany().HasForeignKey(l => l.BookingId).OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(l => l.Token)
            .IsUnique()
            .HasDatabaseName(TokenIndexName);
        entity.HasIndex(l => l.BookingId)
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName(ActiveBookingIndexName);
        entity.HasIndex(l => l.OrgId);
        entity.HasIndex(l => l.AccountUserId);
    }
}
