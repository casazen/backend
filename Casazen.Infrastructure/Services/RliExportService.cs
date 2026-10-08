using System.Text;
using Casazen.Core.Documents;
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
    IPdfDocumentRenderer pdfRenderer,
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
        // LT-09: This is the complete RLI pre-fill package for manual submission to the Agenzia delle Entrate
        // (RLI web / Entratel / intermediario abilitato). CasaZen does NOT submit automatically and stores no
        // Openapi credentials. (PO 2026-10-08)
        // LT-09: Convert to PDF/A before sending. Library not yet configured; the generated file must be converted
        // externally (e.g. via Adobe Acrobat, LibreOffice, or a PDF/A conversion service).
        var pdf = pdfRenderer.Render(PdfDocumentContent.FromPlainText(
            "Pacchetto RLI per invio manuale - non depositato da CasaZen",
            body));

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
        sb.AppendLine("Dataset RLI precompilato per invio manuale - il locatore o il suo intermediario abilitato deposita il file.");
        sb.AppendLine("CasaZen NON deposita questo file e NON e' intermediario abilitato (DPR 322/1998).");
        sb.AppendLine("Nessun invio automatico. Nessuna credenziale Openapi utilizzata. (LT-09, PO 2026-10-08)");
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
        // Every landlord, then every tenant, in the order entered (LT-14): the RLI needs all of them.
        foreach (var party in lease.Parties.OrderBy(p => p.Role).ThenBy(p => p.Position))
        {
            sb.AppendLine($"- {party.Role}: {party.FirstName} {party.LastName} ({party.Citizenship}) CF:{party.FiscalCode}");
        }
        return sb.ToString();
    }
}
