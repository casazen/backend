using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The quote of a service request (SP-04b): the columns of the offer a supplier makes on a request that is still new, and what the
/// database keeps true about them. A quote is there or it is not (all its columns together), it is on a request waiting for the answer
/// (<c>Richiesto</c>), it has from one to twenty lines and a total in the bounds of a request, and its other time has both ends or none. A
/// quote and a proposed time are never both there: one offer at a time. The total being the sum of the lines is the rule of
/// <see cref="ServiceRequestQuoteRules.Normalize"/> (a CHECK cannot add up a JSON array without a function of its own).
/// </summary>
internal sealed class ServiceRequestQuoteConfiguration : IEntityTypeConfiguration<ServiceRequest>
{
    public void Configure(EntityTypeBuilder<ServiceRequest> entity)
    {
        // The JSON columns are added to a table that already has rows: the database gives those the empty list.
        entity.Property(sr => sr.QuoteLinesJson).HasDefaultValue("[]");
        entity.Property(sr => sr.QuoteEventsJson).HasDefaultValue("[]");

        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_ServiceRequests_Quote",
                "(\"QuoteSentAt\" IS NULL AND \"QuoteSentByUserId\" IS NULL AND \"QuoteTotalCents\" IS NULL AND \"QuoteNote\" IS NULL "
                + "AND \"QuoteScheduledStartUtc\" IS NULL AND \"QuoteScheduledEndUtc\" IS NULL AND \"QuoteLinesJson\" = '[]'::jsonb) OR "
                + $"(\"QuoteSentAt\" IS NOT NULL AND \"Status\" = {(int)ServiceRequestStatus.Richiesto} "
                + $"AND \"QuoteTotalCents\" BETWEEN 1 AND {ServiceRequestLimits.MaxAmountCents} "
                + $"AND jsonb_array_length(\"QuoteLinesJson\") BETWEEN 1 AND {ServiceRequestLimits.MaxQuoteLines} "
                + "AND ((\"QuoteScheduledStartUtc\" IS NULL AND \"QuoteScheduledEndUtc\" IS NULL) OR "
                + "(\"QuoteScheduledStartUtc\" IS NOT NULL AND \"QuoteScheduledEndUtc\" IS NOT NULL "
                + "AND \"QuoteScheduledEndUtc\" > \"QuoteScheduledStartUtc\")))");

            t.HasCheckConstraint(
                "CK_ServiceRequests_QuoteOffer",
                "\"QuoteSentAt\" IS NULL OR \"ProposedStartUtc\" IS NULL");
        });
    }
}
