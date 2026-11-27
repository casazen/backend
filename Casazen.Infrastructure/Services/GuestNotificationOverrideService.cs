using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IGuestNotificationOverrideService" />
public sealed class GuestNotificationOverrideService(
    AppDbContext db,
    ILogger<GuestNotificationOverrideService> logger,
    TimeProvider? timeProvider = null) : IGuestNotificationOverrideService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task SetAsync(Guid propertyId, GuestNotificationOverride? value, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (value is { } chosen && !Enum.IsDefined(chosen))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Not a setting of the notification of the guests.");

        // The tenant filter of the context: another org's property is not found.
        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found") { Code = "property_not_found", MessageKey = "PropertyNotFound" };

        if (!SwissPlace.IsSwissCountry(property.Country))
            throw new DomainRuleException(GuestRegistrationErrorCodes.OverrideNotApplicable, GuestRegistrationErrorCodes.OverrideNotApplicableMessageKey);

        if (property.GuestNotificationOverride == value)
            return;

        property.GuestNotificationOverride = value;
        property.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "User {UserId} set the notification of the guests of property {PropertyId} to {Setting}",
            userId, propertyId, value?.ToString() ?? "follow-the-regime");
    }
}
