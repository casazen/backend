using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The sender of a property (ADR-029, E03/F01/US05/T01): one row per property (unique index, a concurrent first choice loses on it), going
/// with the property (cascade) and carrying its org (TN-2, restrict like every other <c>OrgId</c> foreign key). The database refuses an
/// ISTAT sender that is not the Alloggiati one (ADR-029-R5), so two automatic senders can never be stored. The Alloggiati sender is a
/// concurrency token: a change that read another sender than the one stored when it saves loses, instead of overwriting a switch it did
/// not see.
/// </summary>
internal sealed class PropertyComplianceSenderConfiguration : IEntityTypeConfiguration<PropertyComplianceSender>
{
    public void Configure(EntityTypeBuilder<PropertyComplianceSender> entity)
    {
        entity.ToTable(t => t.HasCheckConstraint(
            PropertyComplianceSender.IstatFollowsAlloggiatiConstraintName,
            "\"IstatSender\" = \"AlloggiatiSender\""));
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(s => s.PropertyId)
            .IsUnique()
            .HasDatabaseName(PropertyComplianceSender.PropertyIndexName);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => s.OrgId);
        entity.Property(s => s.AlloggiatiSender).IsConcurrencyToken();
    }
}
