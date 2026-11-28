using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OwnerAccountingPolicyConfiguration : IEntityTypeConfiguration<OwnerAccountingPolicy>
{
    public void Configure(EntityTypeBuilder<OwnerAccountingPolicy> entity)
    {
        entity.HasIndex(p => new { p.OrgId, p.RuleCode, p.Version }).IsUnique().HasDatabaseName(OwnerAccountingPolicy.RuleVersionIndexName);
        entity.HasIndex(p => new { p.OrgId, p.Jurisdiction });
        entity.ToTable(table => table.HasCheckConstraint("CK_OwnerAccountingPolicies_Validity", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\""));
    }
}

/// <summary>
/// The effective-dated fiscal profiles of the owners (NF-109, ADR-012, E06/F05/US02/T01): one profile per owner and first day, the owner and the rule
/// set restrict (a profile is evidence of what applied). The identifiers, the address and the bank account get their converter from
/// <c>EncryptedColumns</c> (no length).
/// </summary>
internal sealed class OwnerFiscalProfileConfiguration : IEntityTypeConfiguration<OwnerFiscalProfile>
{
    public void Configure(EntityTypeBuilder<OwnerFiscalProfile> entity)
    {
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(p => p.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<OwnerAccountingPolicy>().WithMany().HasForeignKey(p => p.AccountingPolicyId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(p => new { p.OrgId, p.PropertyOwnerId, p.ValidFrom }).IsUnique().HasDatabaseName(OwnerFiscalProfile.OwnerValidFromIndexName);
        entity.ToTable(table => table.HasCheckConstraint("CK_OwnerFiscalProfiles_Validity", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\""));
    }
}

internal sealed class OwnerMandateConfiguration : IEntityTypeConfiguration<OwnerMandate>
{
    public void Configure(EntityTypeBuilder<OwnerMandate> entity)
    {
        entity.HasOne<PropertyOwner>().WithMany().HasForeignKey(m => m.PropertyOwnerId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(m => m.PropertyOwnership).WithMany().HasForeignKey(m => m.PropertyOwnershipId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(m => new { m.OrgId, m.PropertyOwnerId, m.ValidFrom }).IsUnique()
            .HasFilter("\"PropertyOwnershipId\" IS NULL")
            .HasDatabaseName(OwnerMandate.OwnerValidFromIndexName);
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OwnerMandates_Validity", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\"");
            table.HasCheckConstraint("CK_OwnerMandates_CommissionPercent", "\"CommissionPercent\" >= 0 AND \"CommissionPercent\" <= 100");
            table.HasCheckConstraint(
                "CK_OwnerMandates_FlatRates",
                "(\"CleaningFlatRate\" IS NULL OR \"CleaningFlatRate\" >= 0) AND (\"LinenFlatRate\" IS NULL OR \"LinenFlatRate\" >= 0)");
        });
    }
}

internal sealed class OrgChannelCommissionRuleConfiguration : IEntityTypeConfiguration<OrgChannelCommissionRule>
{
    public void Configure(EntityTypeBuilder<OrgChannelCommissionRule> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(r => new { r.OrgId, r.Channel, r.ValidFrom }).IsUnique().HasDatabaseName(OrgChannelCommissionRule.ChannelValidFromIndexName);
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_OrgChannelCommissionRules_Percent", "\"Percent\" >= 0 AND \"Percent\" <= 100");
            table.HasCheckConstraint("CK_OrgChannelCommissionRules_Validity", "\"ValidTo\" IS NULL OR \"ValidTo\" >= \"ValidFrom\"");
        });
    }
}
