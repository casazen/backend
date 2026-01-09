using System.Security.Claims;
using System.Text.Json;

namespace Casazen.Infrastructure.External;

public interface IAuth0Service
{
    Task<Dictionary<string, object>> GetUserInfoAsync(string accessToken);
}

public class Auth0Service(HttpClient httpClient, ILogger<Auth0Service> logger, IConfiguration configuration) : IAuth0Service
{
    public async Task<Dictionary<string, object>> GetUserInfoAsync(string accessToken)
    {
        try
        {
            var domain = configuration["Auth0:Domain"];
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://{domain}/userinfo");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");

            var response = await httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
                throw new Exception("Failed to get user info from Auth0");

            var json = await response.Content.ReadAsStringAsync();
            var userInfo = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            
            logger.LogInformation("Retrieved Auth0 user info");
            return userInfo ?? new Dictionary<string, object>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting Auth0 user info");
            throw;
        }
    }
}