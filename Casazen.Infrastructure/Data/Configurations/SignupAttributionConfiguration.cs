using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
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

        // One attribution per org: the first one wins, a parallel insert fails with 23505 and is ignored.
        entity.HasIndex(a => a.OrgId)
            .IsUnique()
            .HasDatabaseName("UIX_SignupAttributions_OrgId");

        entity.HasIndex(a => a.RecordedAt)
            .HasDatabaseName("IX_SignupAttributions_RecordedAt");
    }
}
