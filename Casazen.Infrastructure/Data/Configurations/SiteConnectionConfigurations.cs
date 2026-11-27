using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The one-time codes of the connection of a site (S022, PLG-BE-CONNECT): they go with their org (cascade, like the invitations of an org), the hash is
/// unique (the exchange finds the code by it) and the expiry is indexed for the deletion of the old ones.
/// </summary>
internal sealed class SiteConnectionCodeConfiguration : IEntityTypeConfiguration<SiteConnectionCode>
{
    public void Configure(EntityTypeBuilder<SiteConnectionCode> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(c => c.CodeHash)
            .IsUnique()
            .HasDatabaseName(SiteConnectionCode.CodeHashIndexName);
        entity.HasIndex(c => c.ExpiresAt)
            .HasDatabaseName("IX_SiteConnectionCodes_ExpiresAt");
    }
}

/// <summary>
/// The connected sites (S022, PLG-BE-CONNECT): they go with their org (cascade); the hash of the credential is unique (the scheme <c>SiteToken</c> finds the
/// connection by it), and at most one connection of each org and site is active (filtered unique index): two connections of the same site at the same
/// moment race on it, the loser revokes the winner and takes its place.
/// </summary>
internal sealed class SiteConnectionConfiguration : IEntityTypeConfiguration<SiteConnection>
{
    public void Configure(EntityTypeBuilder<SiteConnection> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(c => c.TokenHash)
            .IsUnique()
            .HasDatabaseName(SiteConnection.TokenHashIndexName);
        entity.HasIndex(c => new { c.OrgId, c.SiteUrl })
            .IsUnique()
            .HasFilter("\"RevokedAt\" IS NULL")
            .HasDatabaseName(SiteConnection.ActiveSiteIndexName);
    }
}
