using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public sealed class ImuNotificationNotReadyException : InvalidOperationException
{
    public ImuNotificationNotReadyException()
        : base("La comunicazione IMU e' esportabile solo per contratti a canone concordato registrati.")
    {
    }
}

public record ImuNotificationExportResult(byte[] PdfBytes, string FileName);

/// <summary>Why the IMU notification cannot be exported or marked sent now (<see cref="ImuNotificationStatusDto"/>), stable for the client.</summary>
public static class ImuNotificationReasonCodes
{
    /// <summary>The lease is not a canone concordato contract: the notification never applies.</summary>
    public const string NotConcordato = "imu_notification_not_concordato";

    /// <summary>Registered leases only (the comune only cares once the contract is on record).</summary>
    public const string LeaseNotRegistered = "imu_notification_lease_not_registered";

    /// <summary>No territorial agreement data for the property's comune (same gate as the rent range, A7-22).</summary>
    public const string DataUnavailable = "imu_notification_data_unavailable";
}

/// <summary>Comune office receiving the IMU communication, with its rate, from the reference data (LT-13, A7-22).</summary>
public sealed record ImuNotificationChannelDto(
    string RecipientOffice,
    string? Email,
    string? Pec,
    string? PostalAddress,
    string? Instructions,
    decimal? RatePercent,
    decimal? EffectiveRatePercent,
    int? RateYear,
    ImuRateKind? RateKind,
    string? RateNotes,
    string? RateSourceUrl,
    string? SourceUrl,
    DataCompleteness DataCompleteness,
    DateTime? LastVerifiedAt);

/// <summary>
/// <c>GET /leases/{id}/canone-concordato/imu-notification</c> response (LT-13, A7-24): whether the notification applies
/// to the lease and whether the backend allows exporting/marking it sent now, with the comune's channel when known.
/// </summary>
public sealed record ImuNotificationStatusDto(
    bool Applicable,
    bool Available,
    string? ReasonCode,
    string Comune,
    ImuNotificationChannelDto? Channel);

public interface IComuneImuNotificationService
{
    /// <summary>IMU notification draft of the lease, or <c>null</c> when it is not visible. The caller authorizes the lease first (TN-3).</summary>
    Task<ImuNotificationExportResult?> ExportAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<bool?> MarkSentAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the notification applies and is exportable now (A7-24: the button must never guess from the lease status
    /// alone), or <c>null</c> when the lease is not visible. The caller authorizes the lease first (TN-3).
    /// </summary>
    Task<ImuNotificationStatusDto?> GetStatusAsync(Guid leaseId, CancellationToken cancellationToken = default);
}
