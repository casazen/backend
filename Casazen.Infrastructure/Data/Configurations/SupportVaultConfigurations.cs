using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// A credential of the support vault (E20/F03/US01/T01, decision O5): one row per org, property and kind, with null-equal semantics (<c>NULLS NOT
/// DISTINCT</c>) so a credential of the whole org is held once too. Unlike the other tenant tables (TN-2, restrict) the row follows its org and its
/// property with a cascade: a credential never outlives what it belongs to, and never blocks its erasure.
/// </summary>
internal sealed class SupportVaultSecretConfiguration : IEntityTypeConfiguration<SupportVaultSecret>
{
    public void Configure(EntityTypeBuilder<SupportVaultSecret> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(s => new { s.OrgId, s.PropertyId, s.Kind })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName(SupportVaultSecret.SecretIndexName);
        entity.HasIndex(s => s.PropertyId);
    }
}

/// <summary>
/// One opening of the support vault (E20/F03/US01/T01): the audit row follows its «Do it for me» request and its org (cascade). The index on the request
/// and the instant is the count of the openings of the last hour.
/// </summary>
internal sealed class SupportVaultAccessConfiguration : IEntityTypeConfiguration<SupportVaultAccess>
{
    public void Configure(EntityTypeBuilder<SupportVaultAccess> entity)
    {
        entity.HasOne<OnboardingAssistRequest>().WithMany().HasForeignKey(a => a.RequestId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(a => a.OrgId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(a => new { a.RequestId, a.AccessedAt });
        entity.HasIndex(a => a.OrgId);
    }
}

/// <summary>
/// The regional ISTAT credentials of a property (E20/F03/US01/T01): one row per property (unique index), following the property and its org with a
/// cascade, like the credentials of the vault they are opened with.
/// </summary>
internal sealed class PropertyIstatCredentialsConfiguration : IEntityTypeConfiguration<PropertyIstatCredentials>
{
    public void Configure(EntityTypeBuilder<PropertyIstatCredentials> entity)
    {
        entity.HasOne<Property>().WithMany().HasForeignKey(c => c.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(c => c.PropertyId).IsUnique();
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(c => c.OrgId);
    }
}
