using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PaymentRefundConfiguration : IEntityTypeConfiguration<PaymentRefund>
{
    public void Configure(EntityTypeBuilder<PaymentRefund> refund)
    {
        refund.HasIndex(r => r.PaymentId);
        refund.HasIndex(r => r.OrgId);
        refund.HasIndex(r => r.StripeRefundId)
            .IsUnique()
            .HasFilter("\"StripeRefundId\" IS NOT NULL");
        refund.HasIndex(r => r.IdempotencyKey).IsUnique();
        refund.HasOne(r => r.Payment)
            .WithMany()
            .HasForeignKey(r => r.PaymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
