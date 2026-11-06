using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The daily receipts of the Alloggiati transmission (AW-R05, ADR-015-R2, E03/F01/US05/T01): one per org, Questura account, day and mode
/// (unique index: a repeated retrieval never stores a second PDF), read by org and day. The org restricts like every other <c>OrgId</c>
/// foreign key (TN-2).
/// </summary>
internal sealed class AlloggiatiReceiptConfiguration : IEntityTypeConfiguration<AlloggiatiReceipt>
{
    public void Configure(EntityTypeBuilder<AlloggiatiReceipt> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(r => r.ContentHash).IsFixedLength();
        entity.HasIndex(r => new { r.OrgId, r.AccountKey, r.ReceiptDate, r.Mode })
            .IsUnique()
            .HasDatabaseName(AlloggiatiReceipt.AccountDayModeIndexName);
        entity.HasIndex(r => new { r.OrgId, r.ReceiptDate });
    }
}
