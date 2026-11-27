using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class CondominiumConfiguration : IEntityTypeConfiguration<Condominium>
{
    public void Configure(EntityTypeBuilder<Condominium> entity)
    {
        entity.HasOne(condominium => condominium.Org).WithMany().HasForeignKey(condominium => condominium.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(condominium => condominium.FiscalStartMonth).HasDefaultValue(6);
        entity.HasIndex(condominium => condominium.OrgId);
        entity.ToTable(table => table.HasCheckConstraint("CK_Condominiums_FiscalStartMonth", "\"FiscalStartMonth\" BETWEEN 1 AND 12"));
    }
}

internal sealed class CondominiumUnitConfiguration : IEntityTypeConfiguration<CondominiumUnit>
{
    public void Configure(EntityTypeBuilder<CondominiumUnit> entity)
    {
        entity.HasOne(unit => unit.Condominium).WithMany(condominium => condominium.Units).HasForeignKey(unit => unit.CondominiumId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(unit => unit.Property).WithMany().HasForeignKey(unit => unit.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(unit => unit.Org).WithMany().HasForeignKey(unit => unit.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(unit => unit.Millesimi).HasPrecision(8, 2);
        entity.HasIndex(unit => unit.CondominiumId);
        entity.HasIndex(unit => new { unit.OrgId, unit.PropertyId });
    }
}

internal sealed class CondominiumPeriodConfiguration : IEntityTypeConfiguration<CondominiumPeriod>
{
    public void Configure(EntityTypeBuilder<CondominiumPeriod> entity)
    {
        entity.HasOne(period => period.Condominium).WithMany(condominium => condominium.Periods).HasForeignKey(period => period.CondominiumId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(period => period.Org).WithMany().HasForeignKey(period => period.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(period => period.Status).HasConversion<int>();
        entity.HasIndex(period => new { period.CondominiumId, period.Label }).IsUnique();
        entity.HasIndex(period => period.OrgId);
        entity.ToTable(table => table.HasCheckConstraint("CK_CondominiumPeriods_Dates", "\"EndDate\" >= \"StartDate\""));
    }
}

internal sealed class CondominiumStatementConfiguration : IEntityTypeConfiguration<CondominiumStatement>
{
    public void Configure(EntityTypeBuilder<CondominiumStatement> entity)
    {
        entity.HasOne(statement => statement.Period).WithMany(period => period.Statements).HasForeignKey(statement => statement.CondominiumPeriodId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(statement => statement.Org).WithMany().HasForeignKey(statement => statement.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(statement => statement.Kind).HasConversion<int>();
        entity.Property(statement => statement.Status).HasConversion<int>();
        entity.Property(statement => statement.TotalAmount).HasPrecision(18, 2);
        entity.HasIndex(statement => new { statement.CondominiumPeriodId, statement.Kind, statement.RevisionNumber }).IsUnique();
        entity.HasIndex(statement => new { statement.CondominiumPeriodId, statement.Kind })
            .IsUnique()
            .HasFilter("\"Status\" = 1");
        entity.HasIndex(statement => statement.OrgId);
    }
}

internal sealed class CondominiumExpenseLineConfiguration : IEntityTypeConfiguration<CondominiumExpenseLine>
{
    public void Configure(EntityTypeBuilder<CondominiumExpenseLine> entity)
    {
        entity.HasOne(line => line.Statement).WithMany(statement => statement.Lines).HasForeignKey(line => line.StatementId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(line => line.Unit).WithMany().HasForeignKey(line => line.CondominiumUnitId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(line => line.Org).WithMany().HasForeignKey(line => line.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(line => line.Amount).HasPrecision(18, 2);
        entity.Property(line => line.RecoverableAmount).HasPrecision(18, 2);
        entity.Property(line => line.RecoverablePercentage).HasPrecision(5, 2);
        entity.Property(line => line.Confidence).HasPrecision(5, 4);
        entity.Property(line => line.ExpenseNature).HasConversion<int>();
        entity.Property(line => line.TenantRecoverability).HasConversion<int>();
        entity.Property(line => line.ClassificationSource).HasConversion<int>();
        entity.Property(line => line.ReviewStatus).HasConversion<int>();
        entity.HasIndex(line => line.StatementId);
        entity.HasIndex(line => line.OrgId);
        entity.ToTable(table => table.HasCheckConstraint(
            "CK_CondominiumExpenseLines_Confidence",
            "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)"));
    }
}

internal sealed class CondominiumInstallmentConfiguration : IEntityTypeConfiguration<CondominiumInstallment>
{
    public void Configure(EntityTypeBuilder<CondominiumInstallment> entity)
    {
        entity.HasOne(installment => installment.Period).WithMany(period => period.Installments).HasForeignKey(installment => installment.CondominiumPeriodId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(installment => installment.Org).WithMany().HasForeignKey(installment => installment.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(installment => installment.OrdinaryAmount).HasPrecision(18, 2);
        entity.Property(installment => installment.PriorSettlementAmount).HasPrecision(18, 2);
        entity.Property(installment => installment.ExtraordinaryAmount).HasPrecision(18, 2);
        entity.Property(installment => installment.TotalAmount).HasPrecision(18, 2);
        entity.Property(installment => installment.Status).HasConversion<int>();
        entity.HasIndex(installment => new { installment.CondominiumPeriodId, installment.SlotIndex }).IsUnique();
        entity.HasIndex(installment => installment.OrgId);
    }
}

internal sealed class CondominiumPriorBalanceConfiguration : IEntityTypeConfiguration<CondominiumPriorBalance>
{
    public void Configure(EntityTypeBuilder<CondominiumPriorBalance> entity)
    {
        entity.HasOne(balance => balance.Period).WithOne(period => period.PriorBalance).HasForeignKey<CondominiumPriorBalance>(balance => balance.CondominiumPeriodId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(balance => balance.SourcePeriod).WithMany().HasForeignKey(balance => balance.SourcePeriodId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(balance => balance.Org).WithMany().HasForeignKey(balance => balance.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(balance => balance.Amount).HasPrecision(18, 2);
        entity.Property(balance => balance.Disposition).HasConversion<int>();
        entity.HasIndex(balance => balance.CondominiumPeriodId).IsUnique();
        entity.HasIndex(balance => balance.OrgId);
    }
}

internal sealed class CondominiumPaymentConfiguration : IEntityTypeConfiguration<CondominiumPayment>
{
    public void Configure(EntityTypeBuilder<CondominiumPayment> entity)
    {
        entity.HasOne(payment => payment.Period).WithMany().HasForeignKey(payment => payment.CondominiumPeriodId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(payment => payment.Installment).WithMany(installment => installment.Payments).HasForeignKey(payment => payment.CondominiumInstallmentId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(payment => payment.PriorBalance).WithMany().HasForeignKey(payment => payment.PriorBalanceId).OnDelete(DeleteBehavior.SetNull);
        entity.HasOne(payment => payment.Org).WithMany().HasForeignKey(payment => payment.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(payment => payment.Amount).HasPrecision(18, 2);
        entity.HasIndex(payment => new { payment.OrgId, payment.ExternalSourceHash })
            .IsUnique()
            .HasFilter("\"ExternalSourceHash\" IS NOT NULL");
        entity.HasIndex(payment => payment.CondominiumPeriodId);
        entity.HasIndex(payment => payment.CondominiumInstallmentId);
    }
}

internal sealed class TenantCondominiumSettlementConfiguration : IEntityTypeConfiguration<TenantCondominiumSettlement>
{
    public void Configure(EntityTypeBuilder<TenantCondominiumSettlement> entity)
    {
        entity.HasOne(settlement => settlement.LeaseContract).WithMany().HasForeignKey(settlement => settlement.LeaseContractId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(settlement => settlement.Period).WithMany().HasForeignKey(settlement => settlement.CondominiumPeriodId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(settlement => settlement.Org).WithMany().HasForeignKey(settlement => settlement.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(settlement => settlement.RecoverableFinalAmount).HasPrecision(18, 2);
        entity.Property(settlement => settlement.AdvancesAccountedAmount).HasPrecision(18, 2);
        entity.Property(settlement => settlement.PriorTenantBalanceAmount).HasPrecision(18, 2);
        entity.Property(settlement => settlement.SettlementAmount).HasPrecision(18, 2);
        entity.Property(settlement => settlement.Direction).HasConversion<int>();
        entity.Property(settlement => settlement.Disposition).HasConversion<int>();
        entity.Property(settlement => settlement.AllocationRule).HasConversion<int>();
        entity.Property(settlement => settlement.Status).HasConversion<int>();
        entity.HasIndex(settlement => new { settlement.LeaseContractId, settlement.StatementId }).IsUnique();
        entity.HasIndex(settlement => new { settlement.OrgId, settlement.CondominiumPeriodId });
    }
}

internal sealed class TenantExpenseAllocationConfiguration : IEntityTypeConfiguration<TenantExpenseAllocation>
{
    public void Configure(EntityTypeBuilder<TenantExpenseAllocation> entity)
    {
        entity.HasOne(allocation => allocation.Settlement).WithMany(settlement => settlement.Allocations).HasForeignKey(allocation => allocation.TenantCondominiumSettlementId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(allocation => allocation.LeaseContract).WithMany().HasForeignKey(allocation => allocation.LeaseContractId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(allocation => allocation.ExpenseLine).WithMany().HasForeignKey(allocation => allocation.CondominiumExpenseLineId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(allocation => allocation.Org).WithMany().HasForeignKey(allocation => allocation.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(allocation => allocation.BaseAmount).HasPrecision(18, 2);
        entity.Property(allocation => allocation.AllocatedAmount).HasPrecision(18, 2);
        entity.Property(allocation => allocation.Rule).HasConversion<int>();
        entity.Property(allocation => allocation.Status).HasConversion<int>();
        entity.HasIndex(allocation => allocation.TenantCondominiumSettlementId);
        entity.HasIndex(allocation => allocation.OrgId);
    }
}

internal sealed class CondominiumImportBatchConfiguration : IEntityTypeConfiguration<CondominiumImportBatch>
{
    public void Configure(EntityTypeBuilder<CondominiumImportBatch> entity)
    {
        entity.HasOne(batch => batch.Org).WithMany().HasForeignKey(batch => batch.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(batch => batch.Status).HasConversion<int>();
        entity.HasIndex(batch => new { batch.OrgId, batch.ContentHash }).IsUnique();
    }
}

internal sealed class CondominiumImportHouseConfiguration : IEntityTypeConfiguration<CondominiumImportHouse>
{
    public void Configure(EntityTypeBuilder<CondominiumImportHouse> entity)
    {
        entity.HasOne(house => house.Batch).WithMany(batch => batch.Houses).HasForeignKey(house => house.BatchId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(house => house.Property).WithMany().HasForeignKey(house => house.PropertyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(house => house.Org).WithMany().HasForeignKey(house => house.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(house => house.Status).HasConversion<int>();
        entity.HasIndex(house => house.BatchId);
        entity.HasIndex(house => new { house.OrgId, house.PropertyId });
    }
}

internal sealed class CondominiumImportYearConfiguration : IEntityTypeConfiguration<CondominiumImportYear>
{
    public void Configure(EntityTypeBuilder<CondominiumImportYear> entity)
    {
        entity.HasOne(year => year.House).WithMany(house => house.Years).HasForeignKey(year => year.HouseId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(year => year.Org).WithMany().HasForeignKey(year => year.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(year => year.Cadence).HasConversion<int>();
        entity.HasIndex(year => year.HouseId);
    }
}

internal sealed class CondominiumStatementProposalConfiguration : IEntityTypeConfiguration<CondominiumStatementProposal>
{
    public void Configure(EntityTypeBuilder<CondominiumStatementProposal> entity)
    {
        entity.HasOne(proposal => proposal.Condominium).WithMany().HasForeignKey(proposal => proposal.CondominiumId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(proposal => proposal.Org).WithMany().HasForeignKey(proposal => proposal.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(proposal => proposal.Kind).HasConversion<int>();
        entity.Property(proposal => proposal.Settlement).HasConversion<int>();
        entity.Property(proposal => proposal.Status).HasConversion<int>();
        entity.Property(proposal => proposal.OrdinaryAmount).HasPrecision(18, 2);
        entity.Property(proposal => proposal.ExtraordinaryAmount).HasPrecision(18, 2);
        entity.Property(proposal => proposal.FinalAmount).HasPrecision(18, 2);
        entity.Property(proposal => proposal.Confidence).HasPrecision(5, 4);
        entity.HasIndex(proposal => new { proposal.OrgId, proposal.CondominiumId, proposal.ContentHash });
    }
}
