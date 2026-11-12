using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The rule sets of the owner accounting (NF-109, NF-110, E06/F05/US02/T01): one version of a rule code per agency. A rule set is never removed while a
/// profile names it (restrict).
/// </summary>
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
