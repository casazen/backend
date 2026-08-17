namespace Casazen.Core.Services;

public sealed class ImuNotificationNotReadyException : InvalidOperationException
{
    public ImuNotificationNotReadyException()
        : base("La comunicazione IMU e' esportabile solo per contratti a canone concordato registrati.")
    {
    }
}

public record ImuNotificationExportResult(byte[] PdfBytes, string FileName);

public interface IComuneImuNotificationService
{
    Task<ImuNotificationExportResult?> ExportAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);

    Task<bool?> MarkSentAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);
}
