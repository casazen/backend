using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SignupAttributionConfiguration : IEntityTypeConfiguration<SignupAttribution>
{
    public void Configure(EntityTypeBuilder<SignupAttribution> entity)
    {
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(a => a.OrgId)
            .IsUnique()
            .HasDatabaseName("UIX_SignupAttributions_OrgId");

        entity.HasIndex(a => a.RecordedAt)
            .HasDatabaseName("IX_SignupAttributions_RecordedAt");
    }
}
