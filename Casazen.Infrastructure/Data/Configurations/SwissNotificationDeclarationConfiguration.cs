using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The declaration «I uploaded the file» of an arrival day (E30/F02/US02/T01, NF-128): going with the property (cascade) and carrying its org (TN-2, restrict like every other
/// <c>OrgId</c> foreign key); the receipt that goes with it, when there is one, leaves it without when the retention removes the receipt (SET NULL, never a delete).
/// </summary>
/// <remarks>
/// The key is two partial unique indexes, as for <see cref="MarketplaceActivation"/>: PostgreSQL takes two NULLs as different values, so one index on (property, day, regime) would
/// let a second declaration of the notification to the Ticino police (no regime code) in. One index serves the rows without a code, the other the rows with it, so another canton
/// (E31) reuses the table with no migration.
/// </remarks>
internal sealed class SwissNotificationDeclarationConfiguration : IEntityTypeConfiguration<SwissNotificationDeclaration>
{
    public void Configure(EntityTypeBuilder<SwissNotificationDeclaration> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(d => d.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(d => d.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SwissComplianceReceipt>()
            .WithMany()
            .HasForeignKey(d => d.ReceiptId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        entity.Property(d => d.Channel).HasDefaultValue(SwissNotificationChannels.Portal);

        entity.HasIndex(d => new { d.PropertyId, d.ArrivalDay })
            .IsUnique()
            .HasFilter("\"RegimeCode\" IS NULL")
            .HasDatabaseName(SwissNotificationDeclaration.DefaultRegimeIndexName);
        entity.HasIndex(d => new { d.PropertyId, d.ArrivalDay, d.RegimeCode })
            .IsUnique()
            .HasFilter("\"RegimeCode\" IS NOT NULL")
            .HasDatabaseName(SwissNotificationDeclaration.NamedRegimeIndexName);
        entity.HasIndex(d => d.OrgId);
        entity.HasIndex(d => d.ReceiptId);
    }
}
