using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The reviews of the channels that the channel manager reports (E05/F06/US01/T01, ADR-032, NF-69). One row for each organization, booking and channel (unique
/// index): two reads of the same booking, or two sync runs, are one row. The words of the guest, the reply of the host and the review of the host are encrypted at
/// rest through the Data Protection converter of <c>EncryptedColumns</c> (purpose <c>Casazen.Review.Content</c>): the columns hold the payload only, so they have no
/// length and their names say what they hold, as <c>OtaMessages.TextEncrypted</c> does. <c>xmin</c> is the concurrency token, so no reply is saved twice. The reviews go with
/// their booking and their property (cascade, like the reviews of the booking site); every <c>OrgId</c> foreign key restricts.
/// </summary>
internal sealed class OtaReviewConfiguration : IEntityTypeConfiguration<OtaReview>
{
    public void Configure(EntityTypeBuilder<OtaReview> entity)
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

        entity.Property(r => r.GuestReviewText).HasColumnName("GuestReviewTextEncrypted");
        entity.Property(r => r.HostReplyText).HasColumnName("HostReplyTextEncrypted");
        entity.Property(r => r.HostReviewJson).HasColumnName("HostReviewJsonEncrypted");

        entity.Property(r => r.Version).IsRowVersion();

        entity.HasIndex(r => new { r.OrgId, r.BookingId, r.Channel })
            .IsUnique()
            .HasDatabaseName(OtaReview.OrgBookingChannelIndexName);

        entity.HasIndex(r => new { r.OrgId, r.PropertyId, r.GuestReviewPublishedAt })
            .HasDatabaseName(OtaReview.PropertyPublishedIndexName);

        entity.HasIndex(r => r.NextPollAt)
            .HasFilter("\"NextPollAt\" IS NOT NULL")
            .HasDatabaseName(OtaReview.NextPollIndexName);

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_OtaReviews_Channel",
                $"\"Channel\" IN ({(int)OtaChannel.Airbnb}, {(int)OtaChannel.Booking}, {(int)OtaChannel.Vrbo}, {(int)OtaChannel.Expedia})");
            t.HasCheckConstraint(
                "CK_OtaReviews_HostReplyState",
                $"\"HostReplyState\" IN ({(int)OtaReviewActionState.None}, {(int)OtaReviewActionState.Sent}, {(int)OtaReviewActionState.Error}, {(int)OtaReviewActionState.ManualVerify})");
            t.HasCheckConstraint(
                "CK_OtaReviews_HostReviewState",
                $"\"HostReviewState\" IN ({(int)OtaReviewActionState.None}, {(int)OtaReviewActionState.Sent}, {(int)OtaReviewActionState.Error}, {(int)OtaReviewActionState.ManualVerify})");
            t.HasCheckConstraint(
                "CK_OtaReviews_HostReviewWindow",
                $"\"HostReviewWindow\" IN ({(int)HostReviewWindow.Unknown}, {(int)HostReviewWindow.Open}, {(int)HostReviewWindow.Sent}, {(int)HostReviewWindow.Closed})");
            t.HasCheckConstraint("CK_OtaReviews_GuestRating", "\"GuestRating\" IS NULL OR \"GuestRating\" BETWEEN 1 AND 5");
        });
    }
}
