using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Refuses an Openapi filing until a completed checkout matches the fixed Openapi + CasaZen quote. The check is scoped to the lease's org.
/// </summary>
public sealed class OpenapiRegistrationPaymentGate(
    AppDbContext db,
    IOptions<CedolareAdvisoryOptions> advisory,
    ILogger<OpenapiRegistrationPaymentGate> logger) : IOpenapiRegistrationPaymentGate
{
    public async Task EnsurePaidAsync(LeaseContract lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var quote = OpenapiRegistrationFee.Quote(lease.MonthlyRent, advisory.Value.Registro);
        if (quote.AmountCents <= 0)
            return;

        var paid = await db.OpenapiLeaseRegistrationPayments.AnyAsync(
            p => p.LeaseContractId == lease.Id
                && p.OrgId == lease.OrgId
                && p.Status == OpenapiRegistrationPaymentStatus.Completed
                && p.AmountCents == quote.AmountCents
                && p.FeePercent == quote.Percent,
            cancellationToken);
        if (paid)
            return;

        logger.LogInformation(
            "OpenAPI registration refused: the filing fee is not paid. LeaseId={LeaseId} OrgId={OrgId} ContractType={ContractType} FeePercent={FeePercent} AmountCents={AmountCents}",
            lease.Id,
            lease.OrgId,
            lease.ContractType,
            quote.Percent,
            quote.AmountCents);
        throw new DomainConflictException(OpenapiRegistrationFee.UnpaidCode, "OpenapiRegistrationUnpaid");
    }
}
