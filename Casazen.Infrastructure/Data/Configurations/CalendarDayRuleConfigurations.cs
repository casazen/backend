using System.Globalization;
using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The price and the rules of one night of a property (E08/F05/US01/T01, NF-36): one row per property and stay date (the database refuses a second
/// one: a concurrent first write of the same night loses on the unique index), going with its property (cascade) and carrying its org (restrict).
/// The check constraint is the last line of defence for the bounds of the values, whoever writes them: a price outside 0.01 to 100000, a stay
/// outside 1 to 365 nights, an advance outside 0 to 730 days and a maximum below its minimum are refused by the database itself. The numbers are
/// those of <see cref="PropertyDayRule"/>, which the rules of the service read as well.
/// </summary>
internal sealed class PropertyDayRuleConfiguration : IEntityTypeConfiguration<PropertyDayRule>
{
    public void Configure(EntityTypeBuilder<PropertyDayRule> entity)
    {
        entity.HasOne<Property>().WithMany().HasForeignKey(r => r.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(r => new { r.PropertyId, r.StayDate })
            .IsUnique()
            .HasDatabaseName(PropertyDayRule.PropertyDateIndexName);
        entity.HasIndex(r => new { r.OrgId, r.PropertyId, r.StayDate });
        entity.ToTable(t => t.HasCheckConstraint(PropertyDayRule.ValuesCheckName, ValuesCheck()));
    }

    // "Price" is null or a price; each stay and advance value is null or inside its bounds; a maximum is not below its minimum when both are set.
    private static string ValuesCheck()
    {
        var minPrice = PropertyDayRule.MinPrice.ToString(CultureInfo.InvariantCulture);
        var maxPrice = PropertyDayRule.MaxPrice.ToString(CultureInfo.InvariantCulture);
        var minStay = PropertyDayRule.MinStayNights.ToString(CultureInfo.InvariantCulture);
        var maxStay = PropertyDayRule.MaxStayNights.ToString(CultureInfo.InvariantCulture);
        var minAdvance = PropertyDayRule.MinAdvance.ToString(CultureInfo.InvariantCulture);
        var maxAdvance = PropertyDayRule.MaxAdvance.ToString(CultureInfo.InvariantCulture);

        return $"(\"Price\" IS NULL OR (\"Price\" >= {minPrice} AND \"Price\" <= {maxPrice})) "
            + $"AND (\"MinNights\" IS NULL OR (\"MinNights\" >= {minStay} AND \"MinNights\" <= {maxStay})) "
            + $"AND (\"MaxNights\" IS NULL OR (\"MaxNights\" >= {minStay} AND \"MaxNights\" <= {maxStay})) "
            + "AND (\"MinNights\" IS NULL OR \"MaxNights\" IS NULL OR \"MaxNights\" >= \"MinNights\") "
            + $"AND (\"MinAdvanceDays\" IS NULL OR (\"MinAdvanceDays\" >= {minAdvance} AND \"MinAdvanceDays\" <= {maxAdvance})) "
            + $"AND (\"MaxAdvanceDays\" IS NULL OR (\"MaxAdvanceDays\" >= {minAdvance} AND \"MaxAdvanceDays\" <= {maxAdvance})) "
            + "AND (\"MinAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" IS NULL OR \"MaxAdvanceDays\" >= \"MinAdvanceDays\")";
    }
}

/// <summary>
/// A change of the calendar the host asked for (E08/F05/US01/T01, NF-36): carrying its org (restrict). The list of the changes of an org reads
/// (<c>OrgId</c>, <c>CreatedAt</c>); the job that sends them reads (<c>State</c>, <c>NextAttemptAt</c>). The summary is jsonb.
/// </summary>
internal sealed class CalendarChangeProcessConfiguration : IEntityTypeConfiguration<CalendarChangeProcess>
{
    public void Configure(EntityTypeBuilder<CalendarChangeProcess> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(p => p.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.Property(p => p.SummaryJson).HasColumnType("jsonb");
        entity.HasIndex(p => new { p.OrgId, p.CreatedAt });
        entity.HasIndex(p => new { p.State, p.NextAttemptAt });
    }
}

/// <summary>
/// One range of dates of one property that a change sends to the channel manager (E08/F05/US01/T01, NF-36): going with its process and with its
/// property (cascade) and carrying its org (restrict). The process reads its chunks by <c>ProcessId</c>; the calendar of a property reads the
/// chunks still on their way by (<c>OrgId</c>, <c>PropertyId</c>, <c>State</c>).
/// </summary>
internal sealed class CalendarChangeChunkConfiguration : IEntityTypeConfiguration<CalendarChangeChunk>
{
    public void Configure(EntityTypeBuilder<CalendarChangeChunk> entity)
    {
        entity.HasOne<CalendarChangeProcess>().WithMany().HasForeignKey(c => c.ProcessId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Property>().WithMany().HasForeignKey(c => c.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(c => c.ProcessId);
        entity.HasIndex(c => new { c.OrgId, c.PropertyId, c.State });
    }
}
