using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class MarketplacePortalConfiguration : IEntityTypeConfiguration<MarketplacePortal>
{
    public void Configure(EntityTypeBuilder<MarketplacePortal> entity)
    {
        // ─── Marketplace (E09/F01, NF-54, NF-56, NF-57, NF-59) ───────────────────
        // The portals of the channel manager are platform data refreshed once a day, unique by the code Octorate gives them.
        entity.HasIndex(p => p.PortalCode)
            .IsUnique()
            .HasDatabaseName(MarketplacePortal.PortalCodeIndexName);
    }
}

internal sealed class MarketplacePartnerConfiguration : IEntityTypeConfiguration<MarketplacePartner>
{
    public void Configure(EntityTypeBuilder<MarketplacePartner> entity)
    {
        // A partner is a row the platform admin enters: unique by its slug, read in the order of its work. Its texts go with it. The checks
        // keep a code that is not a lowercase slug and a site that is not https out of the table, whoever writes the row.
        entity.HasIndex(p => p.Code)
            .IsUnique()
            .HasDatabaseName(MarketplacePartner.CodeIndexName);
        entity.HasIndex(p => new { p.Job, p.SortOrder });
        entity.HasMany(p => p.Texts)
            .WithOne()
            .HasForeignKey(t => t.PartnerId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(MarketplacePartner.CodeSlugCheckName, "\"Code\" ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            t.HasCheckConstraint(MarketplacePartner.WebsiteHttpsCheckName, "\"WebsiteUrl\" LIKE 'https://%'");
        });
    }
}

internal sealed class MarketplacePartnerTextConfiguration : IEntityTypeConfiguration<MarketplacePartnerText>
{
    public void Configure(EntityTypeBuilder<MarketplacePartnerText> entity)
    {
        // One row of texts per partner and language (it or en).
        entity.Property(t => t.FaqJson).HasColumnType("jsonb");
        entity.HasIndex(t => new { t.PartnerId, t.Language })
            .IsUnique()
            .HasDatabaseName(MarketplacePartnerText.PartnerLanguageIndexName);
        entity.ToTable(t => t.HasCheckConstraint(MarketplacePartnerText.LanguageCheckName, "\"Language\" IN ('it', 'en')"));
    }
}

internal sealed class MarketplaceActivationConfiguration : IEntityTypeConfiguration<MarketplaceActivation>
{
    public void Configure(EntityTypeBuilder<MarketplaceActivation> entity)
    {
        // What an org activated. A channel, a tool and a licensed function are per property; a partner is the org's or, when the host declares it
        // for one property, that property's: two partial unique indexes
        // (the database takes two NULLs for different values, so one index would let a second org-level row in). An activation goes with its
        // property (CASCADE; a property is soft-deleted, so in practice it stays). xmin is the concurrency token. The tenant query filter
        // is applied below, to every ITenantOwned entity.
        entity.Property(a => a.RowVersion).IsRowVersion();
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(a => a.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(a => new { a.OrgId, a.Kind, a.TargetCode })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NULL")
            .HasDatabaseName(MarketplaceActivation.OrgLevelIndexName);
        entity.HasIndex(a => new { a.OrgId, a.PropertyId, a.Kind, a.TargetCode })
            .IsUnique()
            .HasFilter("\"PropertyId\" IS NOT NULL")
            .HasDatabaseName(MarketplaceActivation.PropertyLevelIndexName);
        entity.HasIndex(a => new { a.OrgId, a.Status });
    }
}
