using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

public class RliExportService(
    ILeaseContractRepository leases,
    ILeaseEventRepository events,
    TimeProvider? timeProvider = null) : IRliExportService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<RliExportResult?> ExportAsync(
        Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease is null || lease.Property is null)
            return null;

        var body = BuildBody(lease, RliRegistrationDeadline.Resolve(lease, _clock.TodayInRome()));
        var pdf = FiscalPdfWriter.Write(
            "Precompilazione RLI - anteprima, non depositata",
            body);

        await events.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.RliExported,
        });

        return new RliExportResult(pdf, $"rli-prefill-{lease.Id:N}.pdf");
    }

    private static string BuildBody(LeaseContract lease, DateTime? registrationDeadline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Dataset RLI precompilato per revisione del locatore / intermediario abilitato.");
        sb.AppendLine("CasaZen NON deposita questo file. CasaZen non e' intermediario abilitato (DPR 322/1998).");
        sb.AppendLine("Bozza da confermare con legale.");
        sb.AppendLine($"Riferimento contratto: {lease.Id:N}");
        sb.AppendLine($"Comune immobile: {lease.Property.City}");
        sb.AppendLine($"Regime fiscale: {lease.FiscalRegime}");
        sb.AppendLine($"Canone mensile: {lease.MonthlyRent:0.00} EUR");
        sb.AppendLine($"Decorrenza: {lease.StartDate:yyyy-MM-dd} - {lease.EndDate:yyyy-MM-dd}");
        // LT-04: min(stipula, decorrenza) + 30 giorni; "da determinare" finche' manca la data di stipula.
        sb.AppendLine(lease.StipulaDate is { } stipula
            ? $"Data di stipula: {stipula:yyyy-MM-dd}"
            : "Data di stipula: non disponibile");
        sb.AppendLine(registrationDeadline is { } deadline
            ? $"Scadenza registrazione: {deadline:yyyy-MM-dd}"
            : "Scadenza registrazione: da determinare");
        sb.AppendLine("Contraenti:");
        foreach (var party in lease.Parties)
        {
            sb.AppendLine($"- {party.Role}: {party.FirstName} {party.LastName} ({party.Citizenship}) CF:{party.FiscalCode}");
        }
        return sb.ToString();
    }
}
