namespace Casazen.Web.DTOs;

/// <summary>Short-lived signed URL of a private document (FD-07).</summary>
/// <param name="Url">Signed GET URL of the object in the private bucket.</param>
/// <param name="ExpiresAt">UTC instant after which the URL stops working.</param>
public sealed record SignedDocumentUrlResponse(string Url, DateTime ExpiresAt);
