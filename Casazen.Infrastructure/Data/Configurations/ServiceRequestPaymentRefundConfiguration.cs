using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceRequestPaymentRefundConfiguration : IEntityTypeConfiguration<ServiceRequestPaymentRefund>
{
    public void Configure(EntityTypeBuilder<ServiceRequestPaymentRefund> entity)
    {
        entity.HasOne(r => r.ServiceRequestPayment)
            .WithMany()
            .HasForeignKey(r => r.ServiceRequestPaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(r => new { r.ServiceRequestPaymentId, r.Sequence })
            .IsUnique()
            .HasDatabaseName("UIX_ServiceRequestPaymentRefunds_Payment_Sequence");
        entity.HasIndex(r => r.StripeRefundId)
            .IsUnique()
            .HasDatabaseName("UIX_ServiceRequestPaymentRefunds_StripeRefundId")
            .HasFilter("\"StripeRefundId\" IS NOT NULL");
        entity.HasIndex(r => r.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("UIX_ServiceRequestPaymentRefunds_IdempotencyKey")
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        // The sync job looks for the refunds that wait for Stripe, by status.
        entity.HasIndex(r => r.Status);

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServiceRequestPaymentRefunds_Amounts",
                $"\"AmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents} AND \"Sequence\" >= 1 "
                + "AND (\"ApplicationFeeRefundedCents\" IS NULL OR \"ApplicationFeeRefundedCents\" >= 0)");
            // A refund that succeeded says when.
            t.HasCheckConstraint(
                "CK_ServiceRequestPaymentRefunds_Succeeded",
                $"\"Status\" <> {(int)ServicePaymentRefundStatus.Succeeded} OR \"CompletedAt\" IS NOT NULL");
        });
    }
}
