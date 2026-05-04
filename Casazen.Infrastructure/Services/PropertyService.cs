using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class PropertyService(IPropertyRepository repository, ILogger<PropertyService> logger) : IPropertyService
{
    public async Task<Property?> GetPropertyAsync(Guid id)
    {
        return await repository.GetByIdAsync(id);
    }

    public async Task<IEnumerable<Property>> GetOwnerPropertiesAsync(string ownerId)
    {
        return await repository.GetByOwnerAsync(ownerId);
    }

    public async Task<IEnumerable<Property>> GetAllPropertiesAsync()
    {
        return await repository.GetAllAsync();
    }

    public async Task<Property> CreatePropertyAsync(Property property)
    {
        logger.LogInformation("Creating property: {Name}", property.Name);
        return await repository.AddAsync(property);
    }

    public async Task<Property> UpdatePropertyAsync(Property property)
    {
        logger.LogInformation("Updating property: {Id}", property.Id);
        return await repository.UpdateAsync(property);
    }

    public async Task<bool> DeletePropertyAsync(Guid id)
    {
        logger.LogInformation("Deleting property: {Id}", id);
        await repository.DeleteAsync(id);
        return true;
    }

    public async Task<IEnumerable<Property>> SearchAsync(string? city, int? bedrooms, decimal? maxPrice)
    {
        logger.LogInformation("Searching properties: city={City}, bedrooms={Bedrooms}, maxPrice={MaxPrice}", city, bedrooms, maxPrice);
        return await repository.SearchAsync(city, bedrooms, maxPrice);
    }

    public async Task<Property> AddImageAsync(Guid propertyId, string imageUrl)
    {
        var property = await repository.GetByIdAsync(propertyId);
        if (property == null)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        // Add image URL to the list
        property.PhotoUrls.Add(imageUrl);
        property.UpdatedAt = DateTime.UtcNow;

        logger.LogInformation("Adding image to property {PropertyId}: {ImageUrl}", propertyId, imageUrl);
        return await repository.UpdateAsync(property);
    }

    public async Task<Property> RemoveImageAsync(Guid propertyId, int imageIndex)
    {
        var property = await repository.GetByIdAsync(propertyId);
        if (property == null)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        if (imageIndex < 0 || imageIndex >= property.PhotoUrls.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(imageIndex), $"Invalid image index {imageIndex}");
        }

        // Remove image URL from the list
        property.PhotoUrls.RemoveAt(imageIndex);
        property.UpdatedAt = DateTime.UtcNow;

        logger.LogInformation("Removing image at index {Index} from property {PropertyId}", imageIndex, propertyId);
        return await repository.UpdateAsync(property);
    }

    public async Task<Property> ReorderImagesAsync(Guid propertyId, List<string> orderedImageUrls)
    {
        var property = await repository.GetByIdAsync(propertyId);
        if (property == null)
        {
            throw new InvalidOperationException($"Property {propertyId} not found");
        }

        // Validate that all URLs in the new order exist in the current list
        var currentUrls = property.PhotoUrls.ToHashSet();
        if (!orderedImageUrls.All(url => currentUrls.Contains(url)) ||
            orderedImageUrls.Count != property.PhotoUrls.Count)
        {
            throw new InvalidOperationException("Invalid image URLs provided for reordering");
        }

        // Update the order
        property.PhotoUrls = orderedImageUrls;
        property.UpdatedAt = DateTime.UtcNow;

        logger.LogInformation("Reordering images for property {PropertyId}", propertyId);
        return await repository.UpdateAsync(property);
    }
}