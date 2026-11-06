using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The monthly compliance summaries already sent (E03/F02/US01/T01, NF-52): one row per org and month. The unique index is what makes the
/// monthly email idempotent (a second run, a retry or a concurrent run loses on it and sends nothing more). The org restricts like every
/// other <c>OrgId</c> foreign key (TN-2).
/// </summary>
internal sealed class ComplianceMonthlySummaryLogConfiguration : IEntityTypeConfiguration<ComplianceMonthlySummaryLog>
{
    public void Configure(EntityTypeBuilder<ComplianceMonthlySummaryLog> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(l => l.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(l => new { l.OrgId, l.YearMonth })
            .IsUnique()
            .HasDatabaseName(ComplianceMonthlySummaryLog.OrgMonthIndexName);
    }
}
