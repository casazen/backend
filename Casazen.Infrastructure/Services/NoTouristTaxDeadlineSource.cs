using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ITouristTaxDeadlineSource" />
/// <remarks>
/// <b>No source yet: the tourist tax alerts stay silent.</b> No table holds the day by which the tourist tax of a month is due in a comune, and the
/// <c>MunicipalityComplianceConfig</c> of the old plan was never built. CasaZen never uses a guessed day, so this answers «not known» for every
/// comune; where the day comes from is an open point of the plan, and the day it is decided this is the only class that changes.
/// </remarks>
public sealed class NoTouristTaxDeadlineSource : ITouristTaxDeadlineSource
{
    public Task<DateOnly?> DeadlineOfAsync(Guid propertyId, DateOnly periodStart, CancellationToken ct) =>
        Task.FromResult<DateOnly?>(null);
}
