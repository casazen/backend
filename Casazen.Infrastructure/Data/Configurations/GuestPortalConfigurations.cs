using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestSupplierOrderDocumentConfiguration : IEntityTypeConfiguration<GuestSupplierOrderDocument>
{
    public void Configure(EntityTypeBuilder<GuestSupplierOrderDocument> entity)
    {
        entity.HasOne<GuestSupplierOrder>().WithMany().HasForeignKey(d => d.OrderId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(d => new { d.OrderId, d.ContentHash }).IsUnique();
    }
}

internal sealed class GuestCheckInSessionOrgConfiguration : IEntityTypeConfiguration<GuestCheckInSession>
{
    public void Configure(EntityTypeBuilder<GuestCheckInSession> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => s.OrgId);
    }
}

internal sealed class GuestHostExtraOrderConfiguration : IEntityTypeConfiguration<GuestHostExtraOrder>
{
    public void Configure(EntityTypeBuilder<GuestHostExtraOrder> entity)
    {
        entity.HasOne<FiscalDocument>().WithMany().HasForeignKey(x => x.SupplementToDocumentId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.OrgId, x.BookingId });
        entity.HasIndex(x => new { x.OrgId, x.ExtraId });
    }
}

internal sealed class GuestHostExtraConfiguration : IEntityTypeConfiguration<GuestHostExtra>
{
    public void Configure(EntityTypeBuilder<GuestHostExtra> entity)
    {
        entity.HasIndex(x => x.OrgId);
    }
}

internal sealed class GuestCrmProfileConfiguration : IEntityTypeConfiguration<GuestCrmProfile>
{
    public void Configure(EntityTypeBuilder<GuestCrmProfile> entity)
    {
        entity.Property(p => p.MarketingCategoryOverridesJson).HasDefaultValue("{}");
    }
}
