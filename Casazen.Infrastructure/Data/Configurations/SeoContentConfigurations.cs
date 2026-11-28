using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class SeoContentPageConfiguration : IEntityTypeConfiguration<SeoContentPage>
{
    public void Configure(EntityTypeBuilder<SeoContentPage> entity)
    {
        entity.HasIndex(p => new { p.ComuneCode, p.PageType })
            .IsUnique();

        entity.HasIndex(p => p.LegalReviewStatus);
    }

    internal static void ConfigurePublishedRevisionIdForeignKey(EntityTypeBuilder<SeoContentPage> entity)
    {
        entity.HasOne(p => p.PublishedRevision)
            .WithMany()
            .HasForeignKey(p => p.PublishedRevisionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class SeoContentRevisionConfiguration : IEntityTypeConfiguration<SeoContentRevision>
{
    public void Configure(EntityTypeBuilder<SeoContentRevision> entity)
    {
        entity.HasOne(r => r.Page)
            .WithMany(p => p.Revisions)
            .HasForeignKey(r => r.PageId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    internal static void ConfigureContentStatus(EntityTypeBuilder<SeoContentRevision> entity)
    {
        entity.Property(r => r.ContentStatus)
            .HasConversion<string>()
            .HasMaxLength(40)
            .HasDefaultValue(SeoContentStatus.Generated)
            .HasSentinel((SeoContentStatus)(-1));

        entity.HasIndex(r => new { r.PageId, r.GeneratedAt });
    }
}

internal sealed class SeoContentReviewEventConfiguration : IEntityTypeConfiguration<SeoContentReviewEvent>
{
    public void Configure(EntityTypeBuilder<SeoContentReviewEvent> entity)
    {
        entity.Property(e => e.Action)
            .HasConversion<string>()
            .HasMaxLength(20);

        entity.HasOne<SeoContentPage>()
            .WithMany()
            .HasForeignKey(e => e.PageId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<SeoContentRevision>()
            .WithMany()
            .HasForeignKey(e => e.RevisionId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.PageId, e.OccurredAt });
    }
}
