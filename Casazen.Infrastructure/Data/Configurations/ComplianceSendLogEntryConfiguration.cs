using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The log of what was sent to the authorities, when and by whom (E03/F02/US01/T01, NF-47): going with the property (cascade), keeping the
/// line when its stay is deleted (the stay is set to null: the line is the audit of a send that happened), and carrying its org (TN-2,
/// restrict like every other <c>OrgId</c> foreign key).
/// </summary>
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
