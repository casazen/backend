using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The decisions about the regime of the stays of a company tenant (E18/F01/US03/T01, ⚖ I7): gone with the org; append only, found by the org and the booking (more
/// than one row for a booking is the history of its decisions, the latest is in force); the booking is a plain id with no foreign key (the decision is tax evidence and
/// outlives the booking); the regime is one of the two the enum names.
/// </summary>
internal sealed class ShortLetTaxDecisionConfiguration : IEntityTypeConfiguration<ShortLetTaxDecision>
{
    public void Configure(EntityTypeBuilder<ShortLetTaxDecision> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(d => d.OrgId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(d => new { d.OrgId, d.BookingId }).HasDatabaseName(ShortLetTaxDecision.BookingIndexName);
        entity.ToTable(table => table.HasCheckConstraint("CK_ShortLetTaxDecisions_Regime", "\"Regime\" IN (1, 2)"));
    }
}
