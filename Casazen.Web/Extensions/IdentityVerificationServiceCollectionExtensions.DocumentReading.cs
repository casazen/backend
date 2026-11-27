using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

public static partial class IdentityVerificationServiceCollectionExtensions
{
    // E70/F02/US02/T01 (NF-197, ADR-013-R21, decision D5): the check-in reads the identity document in the process only. read-document resolves
    // ICheckInDocumentReader to LocalCheckInDocumentReader (the MRZ, then the visual zone), and AddCasazenAiProvider no longer registers a reader, so no AI
    // provider sees the picture. Unconditional on purpose, the one registration of the plan that waits for no flag: it removes an outflow and adds no
    // behaviour, and no flag can reopen it.
    static partial void AddDocumentReadingServices(IServiceCollection services)
    {
        services.AddScoped<ICheckInDocumentReader, LocalCheckInDocumentReader>();
    }
}
