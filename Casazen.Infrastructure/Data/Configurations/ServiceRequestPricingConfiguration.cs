using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceRequestPricingConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        entity.Property(sr => sr.OptionsJson).HasDefaultValue("[]");
        entity.Property(sr => sr.PriceLinesJson).HasDefaultValue("[]");
        entity.Property(sr => sr.WorkPhotosJson).HasDefaultValue("[]");
    }
}
