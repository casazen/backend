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

public static class ImuNotificationReasonCodes
{
    public const string NotConcordato = "imu_notification_not_concordato";

    public const string LeaseNotRegistered = "imu_notification_lease_not_registered";

    public const string DataUnavailable = "imu_notification_data_unavailable";
}

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

public sealed record ImuNotificationStatusDto(
    bool Applicable,
    bool Available,
    string? ReasonCode,
    string Comune,
    ImuNotificationChannelDto? Channel);

public interface IComuneImuNotificationService
{
    Task<ImuNotificationExportResult?> ExportAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<bool?> MarkSentAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);

    Task<ImuNotificationStatusDto?> GetStatusAsync(Guid leaseId, CancellationToken cancellationToken = default);
}
