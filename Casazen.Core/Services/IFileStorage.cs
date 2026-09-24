namespace Casazen.Core.Services;

/// <summary>
/// Logical buckets of the object storage (decision D8: Supabase Storage through its S3 API).
/// </summary>
public enum StorageBucket
{
    /// <summary>
    /// Public-read objects (property and supplier photos, logos). They are referenced by an
    /// absolute public URL (<see cref="IFileStorage.GetPublicUrl"/>).
    /// </summary>
    Public,

    /// <summary>
    /// Private objects (property documents, guest ID scans, contract PDFs). They never get a public
    /// URL: they are read only through an authenticated endpoint that checks tenant and ownership,
    /// or through a short-lived signed URL issued by such an endpoint.
    /// </summary>
    Private,
}

/// <summary>
/// Durable object storage. Keys are relative, slash-separated paths built with
/// <see cref="StorageKeys"/> (for example <c>properties/{propertyId}/documents/{file}</c>).
/// </summary>
public interface IFileStorage
{
    /// <summary>Stores <paramref name="content"/> under <paramref name="key"/>, overwriting any existing object.</summary>
    Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Opens an object for reading, or returns null when it does not exist. The caller disposes the stream.</summary>
    Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>True when the object exists.</summary>
    Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes an object; deleting a missing object is not an error.</summary>
    Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>Absolute public URL of an object of the <see cref="StorageBucket.Public"/> bucket.</summary>
    string GetPublicUrl(string key);

    /// <summary>
    /// Returns the key of an object of the public bucket when <paramref name="url"/> is a public URL
    /// issued by this storage; null for any other URL (legacy relative paths, external URLs).
    /// </summary>
    string? TryGetPublicKey(string url);

    /// <summary>
    /// Short-lived signed GET URL for an object of the <see cref="StorageBucket.Private"/> bucket, or
    /// null when the provider cannot sign URLs (filesystem provider used in Development and tests).
    /// </summary>
    Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default);
}
