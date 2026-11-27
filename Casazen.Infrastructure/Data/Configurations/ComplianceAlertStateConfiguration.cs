using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The stage of the period and connection alerts of a property (E03/F02/US01/T01, NF-52): one row per property, kind and reference (the
/// unique index the compare-and-set of the alert job relies on), going with the property (cascade) and carrying its org (TN-2, restrict
/// like every other <c>OrgId</c> foreign key).
/// </summary>
internal sealed class ComplianceAlertStateConfiguration : IEntityTypeConfiguration<ComplianceAlertState>
{
    public void Configure(EntityTypeBuilder<ComplianceAlertState> entity)
    {
        entity.HasOne(s => s.Property)
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => s.OrgId);
    }
}
