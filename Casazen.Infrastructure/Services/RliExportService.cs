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
        // LT-09, PO 2026-10-08: complete package for manual RLI (web / Entratel / intermediario). CasaZen does not
        // submit, does not charge bollo or imposta di registro, and stores no Openapi credentials.
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
        sb.AppendLine("Nessun addebito di bollo o imposta di registro in piattaforma.");
        sb.AppendLine("Bozza da confermare con legale.");
        sb.AppendLine();
        sb.AppendLine("== Contratto ==");
        sb.AppendLine($"Riferimento contratto: {lease.Id:N}");
        sb.AppendLine($"Tipo contratto: {lease.ContractType}");
        sb.AppendLine($"Stato: {lease.Status}");
        sb.AppendLine($"Regime fiscale: {lease.FiscalRegime}");
        if (lease.TaxRegime is { } tax)
            sb.AppendLine($"Regime imposta: {tax}");
        sb.AppendLine($"Canone mensile: {lease.MonthlyRent:0.00} EUR");
        if (lease.SecurityDeposit is { } deposit)
            sb.AppendLine($"Deposito cauzionale: {deposit:0.00} EUR");
        sb.AppendLine($"Decorrenza: {lease.StartDate:yyyy-MM-dd} - {lease.EndDate:yyyy-MM-dd}");
        // LT-04: min(stipula, decorrenza) + 30 giorni; "da determinare" finche' manca la data di stipula.
        sb.AppendLine(lease.StipulaDate is { } stipula
            ? $"Data di stipula: {stipula:yyyy-MM-dd}"
            : "Data di stipula: non disponibile");
        sb.AppendLine(registrationDeadline is { } deadline
            ? $"Scadenza registrazione: {deadline:yyyy-MM-dd}"
            : "Scadenza registrazione: da determinare");
        sb.AppendLine();
        sb.AppendLine("== Immobile ==");
        var property = lease.Property;
        sb.AppendLine($"Nome: {property.Name}");
        sb.AppendLine($"Indirizzo: {property.Address}");
        if (!string.IsNullOrWhiteSpace(property.Unit))
            sb.AppendLine($"Interno: {property.Unit}");
        sb.AppendLine($"Comune immobile: {property.City}");
        if (!string.IsNullOrWhiteSpace(property.PostalCode))
            sb.AppendLine($"CAP: {property.PostalCode}");
        if (!string.IsNullOrWhiteSpace(property.ComuneIstatCode))
            sb.AppendLine($"Codice ISTAT comune: {property.ComuneIstatCode}");
        sb.AppendLine(property.HasCadastralData
            ? $"Catasto: foglio {property.CadastralSheet}, particella {property.CadastralParcel}, categoria {property.CadastralCategory}, rendita {property.CadastralIncome:0.00} EUR"
            : "Catasto: dati incompleti — completarli prima dell'invio RLI");
        if (!string.IsNullOrWhiteSpace(property.CadastralSubaltern))
            sb.AppendLine($"Subalterno: {property.CadastralSubaltern}");
        sb.AppendLine();
        sb.AppendLine("Contraenti:");
        // Every landlord, then every tenant, in the order entered (LT-14): the RLI needs all of them.
        foreach (var party in lease.Parties.OrderBy(p => p.Role).ThenBy(p => p.Position))
        {
            sb.AppendLine($"- {party.Role}: {party.FirstName} {party.LastName} ({party.Citizenship}) CF:{party.FiscalCode}");
            if (!string.IsNullOrWhiteSpace(party.ContactEmail))
                sb.AppendLine($"  Email: {party.ContactEmail}");
        }
        sb.AppendLine();
        sb.AppendLine("Istruzioni: convertire in PDF/A se richiesto dall'Agenzia, poi inviare tramite RLI web, Entratel o intermediario abilitato. CasaZen non invia il file.");
        return sb.ToString();
    }
}
