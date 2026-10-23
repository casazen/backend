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

        // A PaymentIntent belongs to one payment: the webhook finds its payment by it (SP-15b).
        entity.HasIndex(p => p.StripePaymentIntentId)
            .IsUnique()
            .HasDatabaseName("UIX_ServiceRequestPayments_StripePaymentIntentId")
            .HasFilter("\"StripePaymentIntentId\" IS NOT NULL");

        entity.HasIndex(p => new { p.SupplierOrgId, p.Status });
        entity.HasIndex(p => p.PayerOrgId);

        // The JSON column is added to no existing rows, but the database gives it the empty list like the other jsonb columns.
        entity.Property(p => p.LineItemsJson).HasDefaultValue("[]");

        // The checks mirror the rules of the service: a bad row is refused by the database too. The amounts follow
        // SupplierCommission (the fee is strictly below the amount, or 0 for none, and the net is what is left), the percentage
        // is the one of the options, a host payer has its org, and a payment that was paid says when and how.
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
        });
    }
}
