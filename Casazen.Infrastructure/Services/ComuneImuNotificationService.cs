using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public class ComuneImuNotificationService(
    ILeaseContractRepository leases,
    ILeaseEventRepository events) : IComuneImuNotificationService
{
    public async Task<ImuNotificationExportResult?> ExportAsync(
        Guid leaseId, string ownerId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadOwnedLeaseAsync(leaseId, ownerId);
        if (lease is null)
            return null;
        if (lease.Status != LeaseStatus.Registered)
            throw new ImuNotificationNotReadyException();

        var city = lease.Property.City;
        var body = BuildBody(lease, city);
        var pdf = FiscalPdfWriter.Write(
            "Bozza comunicazione IMU canone concordato — da rivedere e inviare autonomamente",
            body);

        await events.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.ImuNotificationExported,
        });

        return new ImuNotificationExportResult(pdf, $"comunicazione-imu-{lease.Id:N}.pdf");
    }

    public async Task<bool?> MarkSentAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default)
    {
        var lease = await LoadOwnedLeaseAsync(leaseId, ownerId);
        if (lease is null)
            return null;
        if (lease.Status != LeaseStatus.Registered)
            throw new ImuNotificationNotReadyException();

        await events.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.ImuNotificationMarkedSent,
        });
        return true;
    }

    private async Task<LeaseContract?> LoadOwnedLeaseAsync(Guid leaseId, string ownerId)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease is null || lease.Property is null || lease.Property.OwnerId != ownerId)
            return null;
        return lease;
    }

    private static string BuildBody(LeaseContract lease, string city)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Questa e' una bozza precompilata per il locatore. CasaZen non invia la comunicazione al Comune e non e' un intermediario abilitato.");
        sb.AppendLine($"Riferimento contratto: {lease.Id}");
        sb.AppendLine($"Comune immobile: {city}");
        sb.AppendLine($"Canone mensile dichiarato: {lease.MonthlyRent:0.00} EUR");
        sb.AppendLine($"Decorrenza: {lease.StartDate:yyyy-MM-dd} - {lease.EndDate:yyyy-MM-dd}");
        sb.AppendLine();

        if (city.Equals("Seveso", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("Destinatari noti (canale ufficiale NON univoco — da verificare con Ufficio Tributi):");
            sb.AppendLine("- Email/PEC: protocollo@comune.seveso.mb.it ; comune.seveso@pec.it ; tributi@comune.seveso.mb.it");
            sb.AppendLine("- Portale SPID/CIE/CNS Servizi Sociali: Contratti di locazione a canone concordato");
            sb.AppendLine("Incertezza: la ricerca registra due canali. Questa bozza non sceglie quale sia quello ufficiale.");
        }
        else if (city.Equals("Cesano Maderno", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("Destinatario: U.O. Risorse Tributarie");
            sb.AppendLine("Email: risorse.tributarie@comune.cesano-maderno.mb.it");
            sb.AppendLine("PEC: risorse.finanziarie@pec.comune.cesano-maderno.mb.it");
            sb.AppendLine("IMU: valore derivato anno 2025 = 1,04% x 75% circa 0,78%. NON e' un'aliquota ufficiale pubblicata. Delibera 2026 non reperita alla data della ricerca.");
        }
        else
        {
            sb.AppendLine("Destinatario comunale: da verificare con l'ufficio tributi del Comune.");
        }

        sb.AppendLine();
        sb.AppendLine("Informativa, non consulenza fiscale o legale.");
        return sb.ToString();
    }
}
