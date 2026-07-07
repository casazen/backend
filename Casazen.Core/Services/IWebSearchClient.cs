namespace Casazen.Core.Services;

public interface IWebSearchClient
{
    Task<string?> SearchAsync(string query, CancellationToken cancellationToken = default);
}
