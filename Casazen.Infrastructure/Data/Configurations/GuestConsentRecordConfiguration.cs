using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestConsentRecordConfiguration : IEntityTypeConfiguration<GuestConsentRecord>
{
    public void Configure(EntityTypeBuilder<GuestConsentRecord> entity)
    {
        entity.HasOne(r => r.Guest)
            .WithMany()
            .HasForeignKey(r => r.GuestId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(r => r.OrgId);
        entity.HasIndex(r => new { r.GuestId, r.Purpose, r.RecordedAt });
    }
}
