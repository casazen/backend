using System.Globalization;
using System.Text;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public class ComuneImuNotificationService(
    ILeaseContractRepository leases,
    ILeaseEventRepository events,
    ITerritorialRentAgreementRepository territorialAgreements,
    IComuneImuChannelRepository imuChannels,
    IPdfDocumentRenderer pdfRenderer) : IComuneImuNotificationService
{
    public async Task<ImuNotificationExportResult?> ExportAsync(
        Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease?.Property is null)
            return null;
        if (!await IsReadyForImuNotificationAsync(lease, cancellationToken))
            throw new ImuNotificationNotReadyException();

        var city = lease.Property.City;
        var channel = await imuChannels.GetByComuneAsync(city, cancellationToken);
        var body = BuildBody(lease, city, channel);
        var pdf = pdfRenderer.Render(PdfDocumentContent.FromPlainText(
            "Bozza comunicazione IMU canone concordato — da rivedere e inviare autonomamente",
            body));

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
        if (!await IsReadyForImuNotificationAsync(lease, cancellationToken))
            throw new ImuNotificationNotReadyException();

        await events.AddAsync(new LeaseEvent
        {
            LeaseContractId = lease.Id,
            EventType = LeaseEventType.ImuNotificationMarkedSent,
        });
        return true;
    }

    public async Task<ImuNotificationStatusDto?> GetStatusAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease?.Property is null)
            return null;

        var city = lease.Property.City;
        var channel = await imuChannels.GetByComuneAsync(city, cancellationToken);
        var channelDto = channel is null ? null : ToChannelDto(channel);

        if (lease.ContractType != LeaseContractType.Concordato)
            return new ImuNotificationStatusDto(false, false, ImuNotificationReasonCodes.NotConcordato, city, channelDto);

        if (lease.Status != LeaseStatus.Registered)
        {
            return new ImuNotificationStatusDto(
                true, false, ImuNotificationReasonCodes.LeaseNotRegistered, city, channelDto);
        }

        var agreement = await territorialAgreements.GetByComuneAsync(city, cancellationToken);
        var agreementUsable = agreement is { DataCompleteness: not DataCompleteness.Missing } && agreement.Bands.Count > 0;
        if (!agreementUsable)
            return new ImuNotificationStatusDto(true, false, ImuNotificationReasonCodes.DataUnavailable, city, channelDto);

        return new ImuNotificationStatusDto(true, true, null, city, channelDto);
    }

    private async Task<LeaseContract?> LoadOwnedLeaseAsync(Guid leaseId, string ownerId)
    {
        var lease = await leases.GetByIdWithDetailsAsync(leaseId);
        if (lease is null || lease.Property is null || lease.Property.OwnerId != ownerId)
            return null;
        return lease;
    }

    private async Task<bool> IsReadyForImuNotificationAsync(LeaseContract lease, CancellationToken cancellationToken)
    {
        if (lease.Status != LeaseStatus.Registered ||
            lease.ContractType != LeaseContractType.Concordato)
            return false;

        var agreement = await territorialAgreements.GetByComuneAsync(lease.Property.City, cancellationToken);
        return agreement is { DataCompleteness: not DataCompleteness.Missing } && agreement.Bands.Count > 0;
    }

    private static ImuNotificationChannelDto ToChannelDto(ComuneImuChannel channel) => new(
        channel.RecipientOffice, channel.Email, channel.Pec, channel.PostalAddress, channel.Instructions,
        channel.RatePercent, channel.EffectiveRatePercent, channel.RateYear, channel.RateKind,
        channel.RateNotes, channel.RateSourceUrl, channel.SourceUrl, channel.DataCompleteness, channel.LastVerifiedAt);

    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>
    /// The recipient and rate come from <see cref="ComuneImuChannel"/> (LT-13, A7-22): no comune name is ever literal
    /// here. A comune with no row falls back to "verify with the ufficio tributi".
    /// </summary>
    private static string BuildBody(LeaseContract lease, string city, ComuneImuChannel? channel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Questa e' una bozza precompilata per il locatore. CasaZen non invia la comunicazione al Comune e non e' un intermediario abilitato.");
        sb.AppendLine($"Riferimento contratto: {lease.Id}");
        sb.AppendLine($"Comune immobile: {city}");
        sb.AppendLine($"Canone mensile dichiarato: {lease.MonthlyRent:0.00} EUR");
        sb.AppendLine($"Decorrenza: {lease.StartDate:yyyy-MM-dd} - {lease.EndDate:yyyy-MM-dd}");
        sb.AppendLine();

        if (channel is null || channel.DataCompleteness == DataCompleteness.Missing)
        {
            sb.AppendLine("Destinatario comunale: da verificare con l'ufficio tributi del Comune.");
        }
        else
        {
            sb.AppendLine($"Destinatario: {channel.RecipientOffice}");
            if (!string.IsNullOrWhiteSpace(channel.Email))
                sb.AppendLine($"Email: {channel.Email}");
            if (!string.IsNullOrWhiteSpace(channel.Pec))
                sb.AppendLine($"PEC: {channel.Pec}");
            if (!string.IsNullOrWhiteSpace(channel.PostalAddress))
                sb.AppendLine($"Indirizzo: {channel.PostalAddress}");
            if (!string.IsNullOrWhiteSpace(channel.Instructions))
                sb.AppendLine(channel.Instructions);

            if (channel.RatePercent is { } rate && channel.RateYear is { } year)
            {
                var kind = channel.RateKind == ImuRateKind.Derived
                    ? "valore derivato, NON e' un'aliquota ufficiale pubblicata"
                    : "aliquota ufficiale";
                sb.AppendLine($"IMU: {kind}, anno {year} = {rate.ToString("0.###", ItalianCulture)}%" +
                    (channel.EffectiveRatePercent is { } effective
                        ? $" (effettiva con riduzione: {effective.ToString("0.###", ItalianCulture)}%)"
                        : string.Empty));
                if (!string.IsNullOrWhiteSpace(channel.RateNotes))
                    sb.AppendLine(channel.RateNotes);
            }
            else
            {
                sb.AppendLine("IMU: aliquota non nota, verificare con il Comune.");
            }

            if (channel.DataCompleteness == DataCompleteness.Partial)
                sb.AppendLine("Dato parziale: verificare con l'Ufficio Tributi prima dell'invio.");
        }

        sb.AppendLine();
        sb.AppendLine("Informativa, non consulenza fiscale o legale.");
        return sb.ToString();
    }
}
