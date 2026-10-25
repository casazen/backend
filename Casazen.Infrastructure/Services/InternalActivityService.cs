using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class InternalActivityService(
    AppDbContext context,
    ILogger<InternalActivityService> logger,
    TimeProvider? timeProvider = null) : IInternalActivityService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<InternalActivity> CreateActivityAsync(
        Guid propertyId,
        string staffUserId,
        string activityType,
        DateTime scheduledAt,
        decimal? internalRate = null,
        bool rechargeToOwner = false,
        Guid? bookingId = null,
        string? notes = null,
        string? createdByUserId = null)
    {
        ArgumentNullException.ThrowIfNull(staffUserId);
        ArgumentNullException.ThrowIfNull(activityType);

        // Get the property to inherit the OrgId
        var property = await context.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == propertyId);

        if (property == null)
            throw new NotFoundException("Property not found.");

        var activity = new InternalActivity
        {
            PropertyId = propertyId,
            OrgId = property.OrgId,
            StaffUserId = staffUserId,
            ActivityType = activityType,
            ScheduledAt = scheduledAt,
            InternalRate = internalRate,
            RechargeToOwner = rechargeToOwner,
            BookingId = bookingId,
            Notes = notes,
            Status = "Pending",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            CreatedByUserId = createdByUserId
        };

        context.InternalActivities.Add(activity);
        await context.SaveChangesAsync();

        logger.LogInformation(
            "Created internal activity {ActivityId} of type {Type} for property {PropertyId} assigned to staff {StaffId}",
            activity.Id, activityType, propertyId, staffUserId);

        return activity;
    }

    public async Task<InternalActivity> UpdateStatusAsync(Guid activityId, string status, string? notes = null)
    {
        ArgumentNullException.ThrowIfNull(status);

        var activity = await context.InternalActivities
            .FirstOrDefaultAsync(a => a.Id == activityId);

        if (activity == null)
            throw new NotFoundException("Activity not found.");

        activity.Status = status;

        if (notes != null)
            activity.Notes = notes;

        if (status == "Completed" && activity.CompletedAt == null)
            activity.CompletedAt = _clock.GetUtcNow().UtcDateTime;

        await context.SaveChangesAsync();

        logger.LogInformation(
            "Updated activity {ActivityId} status to {Status}",
            activityId, status);

        return activity;
    }

    public async Task<IReadOnlyList<InternalActivity>> GetActivitiesAsync(
        HostScope scope,
        Guid? propertyId = null,
        string? staffUserId = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.InternalActivities
            .InScope(scope)
            .Include(a => a.Property)
            .Include(a => a.Booking)
            .AsQueryable();

        if (propertyId.HasValue)
            query = query.Where(a => a.PropertyId == propertyId.Value);

        if (!string.IsNullOrEmpty(staffUserId))
            query = query.Where(a => a.StaffUserId == staffUserId);

        return await query
            .OrderByDescending(a => a.ScheduledAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InternalActivity>> GetMyActivitiesAsync(
        HostScope scope,
        string staffUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staffUserId);

        return await context.InternalActivities
            .InScope(scope)
            .Where(a => a.StaffUserId == staffUserId)
            .Include(a => a.Property)
            .Include(a => a.Booking)
            .OrderByDescending(a => a.ScheduledAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<InternalActivity?> GetActivityAsync(Guid activityId, CancellationToken cancellationToken = default)
    {
        return await context.InternalActivities
            .Include(a => a.Property)
            .Include(a => a.Booking)
            .FirstOrDefaultAsync(a => a.Id == activityId, cancellationToken);
    }
}
