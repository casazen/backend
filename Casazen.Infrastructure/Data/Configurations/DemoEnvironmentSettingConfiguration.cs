using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class DemoEnvironmentSettingConfiguration : IEntityTypeConfiguration<DemoEnvironmentSetting>
{
    public void Configure(EntityTypeBuilder<DemoEnvironmentSetting> entity)
    {
        entity.Property(s => s.TemplateVersion).HasMaxLength(40);
        entity.Property(s => s.LastResetBy).HasMaxLength(255);
    }
}
