using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class ServicePaymentDisputeConfiguration : IEntityTypeConfiguration<ServicePaymentDispute>
{
    public const string OpenPerPaymentIndexName = "UIX_ServicePaymentDisputes_OpenPerPayment";

    public void Configure(EntityTypeBuilder<ServicePaymentDispute> entity)
    {
        entity.HasOne(d => d.ServiceRequestPayment)
            .WithMany()
            .HasForeignKey(d => d.ServiceRequestPaymentId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ServiceRequest>()
            .WithMany()
            .HasForeignKey(d => d.ServiceRequestId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(d => d.SupplierOrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(d => d.CustomerText).HasColumnName("CustomerTextEncrypted");
        entity.Property(d => d.SupplierReply).HasColumnName("SupplierReplyEncrypted");
        entity.Property(d => d.AdminNote).HasColumnName("AdminNoteEncrypted");

        // One open dispute per payment: a second complaint is the same dispute. The service checks it under the payment lock; this is what makes a
        // second open dispute impossible even if a path did not.
        entity.HasIndex(d => d.ServiceRequestPaymentId)
            .IsUnique()
            .HasDatabaseName(OpenPerPaymentIndexName)
            .HasFilter(
                $"\"Status\" IN ({(int)ServiceDisputeStatus.AwaitingSupplier}, {(int)ServiceDisputeStatus.AwaitingDecision})");

        entity.HasIndex(d => d.ServiceRequestId).HasDatabaseName("IX_ServicePaymentDisputes_ServiceRequestId");

        entity.HasIndex(d => new { d.SupplierOrgId, d.Status }).HasDatabaseName("IX_ServicePaymentDisputes_SupplierOrgId_Status");
        entity.HasIndex(d => new { d.Status, d.SupplierReplyDueAt }).HasDatabaseName("IX_ServicePaymentDisputes_Status_SupplierReplyDueAt");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServicePaymentDisputes_Status",
                $"\"Status\" BETWEEN {(int)ServiceDisputeStatus.AwaitingSupplier} AND {(int)ServiceDisputeStatus.Withdrawn}");

            t.HasCheckConstraint(
                "CK_ServicePaymentDisputes_Resolved",
                $"(\"Status\" IN ({(int)ServiceDisputeStatus.AwaitingSupplier}, {(int)ServiceDisputeStatus.AwaitingDecision}) AND \"ResolvedAt\" IS NULL) "
                + $"OR (\"Status\" IN ({(int)ServiceDisputeStatus.ResolvedForSupplier}, {(int)ServiceDisputeStatus.ResolvedForCustomer}, {(int)ServiceDisputeStatus.Withdrawn}) "
                + "AND \"ResolvedAt\" IS NOT NULL)");

            t.HasCheckConstraint(
                "CK_ServicePaymentDisputes_Refund",
                $"(\"Status\" = {(int)ServiceDisputeStatus.ResolvedForCustomer} "
                + "AND \"DecidedRefundCents\" IS NOT NULL AND \"DecidedRefundCents\" >= 0 "
                + "AND \"RefundTargetCents\" IS NOT NULL AND \"RefundTargetCents\" >= \"DecidedRefundCents\") "
                + "OR (\"DecidedRefundCents\" IS NULL AND \"RefundTargetCents\" IS NULL AND \"SettledAt\" IS NULL)");
        });
    }
}
