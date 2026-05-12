using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class PropertyDocumentRepository(AppDbContext context) : IPropertyDocumentRepository
{
    public async Task<IEnumerable<PropertyDocument>> GetByPropertyIdAsync(Guid propertyId)
    {
        return await context.PropertyDocuments
            .Where(d => d.PropertyId == propertyId)
            .ToListAsync();
    }

    public async Task<PropertyDocument?> GetByIdAsync(Guid id)
    {
        return await context.PropertyDocuments
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<PropertyDocument> AddAsync(PropertyDocument document)
    {
        context.PropertyDocuments.Add(document);
        await context.SaveChangesAsync();
        return document;
    }

    public async Task DeleteAsync(Guid id)
    {
        var document = await context.PropertyDocuments.FindAsync(id);
        if (document != null)
        {
            context.PropertyDocuments.Remove(document);
            await context.SaveChangesAsync();
        }
    }
}
