using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ComplianceSendLogEntryConfiguration : IEntityTypeConfiguration<ComplianceSendLogEntry>
{
    public void Configure(EntityTypeBuilder<ComplianceSendLogEntry> entity)
    {
        entity.HasOne(e => e.Property)
            .WithMany()
            .HasForeignKey(e => e.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Booking>()
            .WithMany()
            .HasForeignKey(e => e.BookingId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasOne<Org>().WithMany().HasForeignKey(e => e.OrgId).OnDelete(DeleteBehavior.Restrict);
    }
}
