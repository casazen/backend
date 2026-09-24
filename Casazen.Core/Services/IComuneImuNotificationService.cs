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
    /// <summary>IMU notification draft of the lease, or <c>null</c> when it is not visible. The caller authorizes the lease first (TN-3).</summary>
    Task<ImuNotificationExportResult?> ExportAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<bool?> MarkSentAsync(Guid leaseId, string ownerId, CancellationToken cancellationToken = default);
}
