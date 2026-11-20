using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The audit of the files generated for the police notification (E30/F02/US02/T01, NF-128): going with the property (cascade) and carrying its org (TN-2, restrict like every other
/// <c>OrgId</c> foreign key). The format is one of <see cref="SwissNotificationFileFormats.All"/> and the rows are never negative: the database keeps any other writer honest. No personal
/// data in any column.
/// </summary>
internal sealed class SwissNotificationFileEventConfiguration : IEntityTypeConfiguration<SwissNotificationFileEvent>
{
    /// <summary>Name of the check that bounds <see cref="SwissNotificationFileEvent.Format"/>.</summary>
    public const string FormatCheckName = "CK_SwissNotificationFileEvents_Format";

    /// <summary>Name of the check that keeps <see cref="SwissNotificationFileEvent.RowCount"/> from being negative.</summary>
    public const string RowCountCheckName = "CK_SwissNotificationFileEvents_RowCount";

    public void Configure(EntityTypeBuilder<SwissNotificationFileEvent> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(e => e.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(e => e.OrgId).OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(e => new { e.PropertyId, e.ArrivalDay, e.CreatedAt });
        entity.HasIndex(e => e.OrgId);

        var formats = string.Join(", ", SwissNotificationFileFormats.All.Select(f => $"'{f}'"));
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(FormatCheckName, $"\"Format\" IN ({formats})");
            t.HasCheckConstraint(RowCountCheckName, "\"RowCount\" >= 0");
        });
    }
}
