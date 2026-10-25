using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The practices of the simulated ConcordatoFacile module (CF-SIM). The table exists in every environment so that the migrations stay
/// the same everywhere, but it is read and written only with <c>ConcordatoFacile:FormMode=Simulated</c>, which production refuses.
/// </summary>
internal sealed class CfSimulatedPracticeConfiguration : IEntityTypeConfiguration<CfSimulatedPractice>
{
    public void Configure(EntityTypeBuilder<CfSimulatedPractice> entity)
    {
        entity.ToTable(table => table.HasComment(
            "solo per ambienti di prova: pratiche del modulo simulato di ConcordatoFacile (ConcordatoFacile:FormMode=Simulated); "
            + "vuota e inerte con FormMode=Real, la sola modalita di produzione"));

        entity.HasIndex(p => p.PracticeId).IsUnique();

        // The practices of a contract, newest first (the list of the status API).
        entity.HasIndex(p => new { p.LeaseId, p.SubmittedAt });
    }
}
