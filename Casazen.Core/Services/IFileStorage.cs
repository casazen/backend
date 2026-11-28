namespace Casazen.Core.Services;

public enum StorageBucket
{
    Public,

    /// <summary>
    /// Private objects (property documents, guest ID scans, contract PDFs). They never get a public
    /// URL: they are read only through an authenticated endpoint that checks tenant and ownership,
    /// or through a short-lived signed URL issued by such an endpoint.
    /// </summary>
    Private,
}

public interface IFileStorage
{
    Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    string GetPublicUrl(string key);

    string? TryGetPublicKey(string url);

    Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default);
}
