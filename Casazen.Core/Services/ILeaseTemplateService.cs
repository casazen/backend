using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface ILeaseTemplateService
{
    Task<byte[]> GeneratePdfAsync(LeaseContract lease);
}
