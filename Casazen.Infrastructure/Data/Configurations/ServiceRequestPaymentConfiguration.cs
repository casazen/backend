using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServiceRequestPaymentConfiguration : IEntityTypeConfiguration<ServiceRequestPayment>
{
    public void Configure(EntityTypeBuilder<ServiceRequestPayment> entity)
    {
        entity.HasOne(p => p.ServiceRequest)
            .WithMany()
            .HasForeignKey(p => p.ServiceRequestId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(p => p.SupplierOrg)
            .WithMany()
            .HasForeignKey(p => p.SupplierOrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(p => p.PayerOrg)
            .WithMany()
            .HasForeignKey(p => p.PayerOrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(p => p.ServiceRequestId)
            .IsUnique()
            .HasDatabaseName(ServiceCharges.LivePaymentIndexName)
            .HasFilter($"\"Status\" <> {(int)ServicePaymentStatus.Canceled}");

        entity.HasIndex(p => p.StripePaymentIntentId)
            .IsUnique()
            .HasDatabaseName("UIX_ServiceRequestPayments_StripePaymentIntentId")
            .HasFilter("\"StripePaymentIntentId\" IS NOT NULL");

        entity.HasIndex(p => p.StripeTransferId)
            .IsUnique()
            .HasDatabaseName("UIX_ServiceRequestPayments_StripeTransferId")
            .HasFilter("\"StripeTransferId\" IS NOT NULL");

        entity.HasIndex(p => new { p.Status, p.ReleaseAt })
            .HasDatabaseName("IX_ServiceRequestPayments_Held_Status_ReleaseAt")
            .HasFilter($"\"FundsModel\" = {(int)ServiceFundsModel.PlatformHeld}");

        entity.HasIndex(p => new { p.SupplierOrgId, p.Status });
        entity.HasIndex(p => p.PayerOrgId);

        entity.Property(p => p.LineItemsJson).HasDefaultValue("[]");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServiceRequestPayments_Amounts",
                $"\"AmountCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents} AND \"ApplicationFeeCents\" >= 0 "
                + "AND \"ApplicationFeeCents\" < \"AmountCents\" AND \"NetCents\" = \"AmountCents\" - \"ApplicationFeeCents\" "
                + "AND \"RefundedCents\" BETWEEN 0 AND \"AmountCents\" AND \"PaymentIntentCount\" >= 0 AND \"SentCount\" >= 0");
            t.HasCheckConstraint(
                "CK_ServiceRequestPayments_CommissionPercent",
                $"\"CommissionPercent\" BETWEEN 0 AND {(int)SupplierPaymentsOptions.MaxCommissionPercent}");
            t.HasCheckConstraint(
                "CK_ServiceRequestPayments_FeeVat",
                "(\"FeeVatMode\" IS NULL AND \"FeeVatCents\" IS NULL) OR "
                + "(\"FeeVatMode\" IS NOT NULL AND \"FeeVatCents\" IS NOT NULL AND \"FeeVatCents\" >= 0)");
            t.HasCheckConstraint(
                "CK_ServiceRequestPayments_Payer",
                $"(\"PayerKind\" = {(int)ServicePayerKind.Host} AND \"PayerOrgId\" IS NOT NULL) OR \"PayerKind\" = {(int)ServicePayerKind.Private}");
            t.HasCheckConstraint(
                "CK_ServiceRequestPayments_Paid",
                $"\"Status\" NOT IN ({(int)ServicePaymentStatus.Paid}, {(int)ServicePaymentStatus.PartiallyRefunded}, {(int)ServicePaymentStatus.Refunded}) "
                + "OR (\"PaidAt\" IS NOT NULL AND \"PaidVia\" IS NOT NULL)");

            t.HasCheckConstraint(
                "CK_ServiceRequestPayments_Funds",
                $"\"FundsModel\" IN ({(int)ServiceFundsModel.DirectCharge}, {(int)ServiceFundsModel.PlatformHeld}) "
                + $"AND (\"Status\" < {(int)ServicePaymentStatus.Authorized} OR \"FundsModel\" = {(int)ServiceFundsModel.PlatformHeld}) "
                + $"AND (\"FundsModel\" = {(int)ServiceFundsModel.DirectCharge} OR \"ConnectedAccountId\" IS NULL)");

            t.HasCheckConstraint(
                "CK_ServiceRequestPayments_Held",
                $"(\"Status\" NOT IN ({(int)ServicePaymentStatus.Held}, {(int)ServicePaymentStatus.Released}, {(int)ServicePaymentStatus.Disputed}, {(int)ServicePaymentStatus.TransferFailed}) "
                + "OR \"CapturedAt\" IS NOT NULL) "
                + $"AND (\"Status\" <> {(int)ServicePaymentStatus.Released} OR (\"StripeTransferId\" IS NOT NULL AND \"ReleasedAt\" IS NOT NULL)) "
                + "AND \"TransferAttempts\" >= 0");
        });
    }
}
