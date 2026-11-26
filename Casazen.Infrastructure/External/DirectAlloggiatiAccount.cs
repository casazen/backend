using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.External;

public sealed record AlloggiatiTransportEnvironment(bool Live);

public sealed class DirectAlloggiatiAccount(AppDbContext db, DirectAlloggiatiSoap soap,
    AlloggiatiTransportEnvironment environment, TimeProvider clock) : IDirectAlloggiatiAccount
{
    public async Task<bool> VerifyAsync(Guid propertyId, CancellationToken ct)
    {
        if (!environment.Live) return false;
        var account = await db.PropertyQuesturaCredentials.AsNoTracking().SingleOrDefaultAsync(c => c.PropertyId == propertyId, ct);
        if (account == null) return false;
        bool verified;
        try
        {
            var token = await soap.TokenAsync(account, ct);
            verified = await soap.VerifyAsync(account, token, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException or InvalidDataException or System.Xml.XmlException)
        {
            verified = false;
        }
        // A change of credentials during the call invalidates the result.
        var written = await db.PropertyQuesturaCredentials.Where(c => c.Id == account.Id && c.UpdatedAt == account.UpdatedAt && c.CredentialVersion == account.CredentialVersion)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.LastVerificationOk, verified)
                .SetProperty(c => c.LastVerifiedAt, clock.GetUtcNow().UtcDateTime)
                .SetProperty(c => c.LastVerificationCode, verified ? "verified" : "alloggiati_authentication_failed"), ct);
        return verified && written == 1;
    }
}
