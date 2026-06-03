using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IPropertyDocumentRepository
{
    Task<IEnumerable<PropertyDocument>> GetByPropertyIdAsync(Guid propertyId);
    Task<PropertyDocument?> GetByIdAsync(Guid id);
    Task<PropertyDocument> AddAsync(PropertyDocument document);
    Task DeleteAsync(Guid id);
}
