using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertyComuneResolver" />
public class PropertyComuneResolver(IComuneDirectory directory) : IPropertyComuneResolver
{
    public async Task<PropertyComune> ResolveAsync(string istatCode, CancellationToken cancellationToken = default)
    {
        var code = istatCode?.Trim() ?? string.Empty;

        if (!await directory.IsAvailableAsync(cancellationToken))
            throw new DomainRuleException(ComuneErrorCodes.DatasetUnavailable, ComuneErrorCodes.DatasetUnavailableMessageKey);

        var comune = await directory.FindByIstatCodeAsync(code, activeOnly: true, cancellationToken);
        if (comune is null || comune.RegionCode is not { } regionCode)
            throw new DomainRuleException(ComuneErrorCodes.IstatUnknown, ComuneErrorCodes.IstatUnknownMessageKey, code);

        return new PropertyComune(comune.IstatCode, comune.Name, regionCode);
    }
}
