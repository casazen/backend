using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class DeviceRegistrationConfiguration : IEntityTypeConfiguration<DeviceRegistration>
{
    public void Configure(EntityTypeBuilder<DeviceRegistration> entity)
    {
        entity.HasOne(d => d.Org)
            .WithMany()
            .HasForeignKey(d => d.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(d => new { d.UserId, d.DeviceId })
            .IsUnique()
            .HasDatabaseName("UIX_DeviceRegistrations_UserId_DeviceId");

        entity.HasIndex(d => d.UserId)
            .HasDatabaseName("IX_DeviceRegistrations_UserId");
    }
}
