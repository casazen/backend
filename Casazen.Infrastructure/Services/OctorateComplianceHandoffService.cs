using System.Security.Cryptography;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IOctorateComplianceHandoffService"/> (E03/F05/US01/T01, CHK-15): prepares the passage of a host to the Octorate area that
/// holds an Alloggiati or ISTAT outcome, and records that it started. Nothing else happens: the service never calls Octorate, never
/// reads or stores an outcome, a credential, a cookie or a guest, and writes no compliance row, no proof and no status.
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> The property (and the booking, when given) are read in the org of <see cref="HostScope"/> and narrowed by
/// <c>InScope</c>, and the booking must belong to the property: a property or a booking that does not exist, belongs to another org, is
/// out of the reach of the caller, or does not match gives the same <see cref="NotFoundException"/>, so the answer tells nothing about
/// which check refused it.</para>
/// <para><b>Mapping.</b> The property needs a confirmed mapping to an Octorate listing (<c>OctorateListingMapping</c>): only then is its
/// Alloggiati and ISTAT handled in the panel. The listing's name, as Octorate gave it, goes in the answer so the host knows which
/// structure to open there.</para>
/// <para><b>Destination.</b> One address from the configuration (<see cref="OctorateComplianceHandoffOptions.ResolveDestination"/>):
/// the verified deep link of the topic, else the authenticated home of the panel, both on the allowlist. There is no address in the
/// request. No booking, guest or period is ever added to it.</para>
/// <para><b>Audit.</b> One line of the activity log of the org (<c>OctorateComplianceHandoffStarted</c>: org, actor, property, topic,
/// optional booking and period, the instant, and the opaque handoff id), staged by <see cref="IActivityLog.Record"/> and written by the
/// only <c>SaveChanges</c> of the call. The log holds ids and short codes, never a name.</para>
/// </remarks>
public sealed class OctorateComplianceHandoffService(
    AppDbContext db,
    IActivityLog activityLog,
    IOptions<OctorateComplianceHandoffOptions> options,
    IOptions<OctorateOptions> octorateOptions,
    ILogger<OctorateComplianceHandoffService> logger,
    TimeProvider? timeProvider = null) : IOctorateComplianceHandoffService
{
    /// <summary>404: the property or the booking is not the caller's, is out of its reach, or the booking is not the property's.</summary>
    public const string TargetNotFoundCode = "octorate_handoff_target_not_found";

    /// <summary>409: the property has no confirmed mapping to a listing on Octorate, so there is nothing to check there.</summary>
    public const string PropertyNotMappedCode = "octorate_handoff_property_not_mapped";

    /// <summary>409: no destination is configured (or none passes the allowlist): the host stays in CasaZen.</summary>
    public const string DestinationUnavailableCode = "octorate_handoff_destination_unavailable";

    /// <summary>The member of the 409 <see cref="DestinationUnavailableCode"/> problem that names the guide the client can still show.</summary>
    public const string GuideCodeExtension = "guideCode";

    /// <summary>422: the route to come back to is not a relative CasaZen route.</summary>
    public const string ReturnPathInvalidCode = "octorate_handoff_return_path_invalid";

    /// <summary>422: the period is not a month or a day.</summary>
    public const string PeriodInvalidCode = "octorate_handoff_period_invalid";

    /// <summary>Bytes of randomness of a handoff id (128 bits, written as 32 hexadecimal characters).</summary>
    private const int HandoffIdBytes = 16;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OctorateHandoff> StartAsync(
        HostScope scope,
        string actorUserId,
        OctorateHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        if (!Enum.IsDefined(request.Topic))
            throw new ArgumentOutOfRangeException(nameof(request), request.Topic, "Unknown handoff topic.");

        // What the caller wrote is checked before anything is read.
        if (!OctorateHandoffInput.TryNormalizeReturnPath(request.ReturnPath, out var returnPath))
            throw new DomainRuleException(ReturnPathInvalidCode, "OctorateHandoffReturnPathInvalid");

        if (!OctorateHandoffInput.TryNormalizePeriod(request.Period, out var period))
            throw new DomainRuleException(PeriodInvalidCode, "OctorateHandoffPeriodInvalid");

        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == scope.OrgId && p.Id == request.PropertyId)
            .InScope(scope)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw TargetNotFound();

        var booking = request.BookingId is { } bookingId
            ? await db.Bookings
                .AsNoTracking()
                .Where(b => b.OrgId == scope.OrgId && b.Id == bookingId && b.PropertyId == property.Id)
                .InScope(scope)
                .Select(b => new { b.Id, b.BookingCode, b.CheckInDate, b.CheckOutDate })
                .FirstOrDefaultAsync(cancellationToken)
              ?? throw TargetNotFound()
            : null;

        var mapping = await db.OctorateListingMappings
            .AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId && m.PropertyId == property.Id && m.Status == OctorateMappingStatus.Confirmed)
            .Select(m => new { m.SnapshotName })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DomainConflictException(PropertyNotMappedCode, "OctorateHandoffPropertyNotMapped");

        var destination = options.Value.ResolveDestination(request.Topic, octorateOptions.Value.FrontendBaseUrl, out var deepLinkRejected);
        if (deepLinkRejected)
        {
            // The address is configuration, but it is not echoed: the log names the topic only.
            logger.LogWarning(
                "The verified Octorate deep link for {Topic} is not an allowlisted https address: the handoff does not use it",
                request.Topic);
        }

        if (destination is null)
        {
            logger.LogWarning("No Octorate destination is configured for the {Topic} handoff of org {OrgId}", request.Topic, scope.OrgId);

            // The host stays in CasaZen and still gets the guide: the client shows the texts of this code and offers a retry.
            throw new DomainConflictException(DestinationUnavailableCode, "OctorateHandoffDestinationUnavailable")
            {
                Extensions = new Dictionary<string, object?>
                {
                    [GuideCodeExtension] = OctorateHandoffGuides.For(request.Topic, booking?.CheckInDate, booking?.CheckOutDate),
                },
            };
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var handoffId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(HandoffIdBytes));

        // The line says that the passage started and nothing about what the host will see: ids and short codes only.
        var details = new List<(string Key, string Value)>
        {
            (OrgActivityDetailKeys.Topic, request.Topic.ToString()),
            (OrgActivityDetailKeys.Handoff, handoffId),
        };
        if (booking is not null)
            details.Add((OrgActivityDetailKeys.Booking, booking.Id.ToString()));
        if (period is not null)
            details.Add((OrgActivityDetailKeys.Period, period));

        activityLog.Record(OrgActivity.Of(
            scope.OrgId,
            OrgActivityType.OctorateComplianceHandoffStarted,
            actorUserId,
            property.Id.ToString(),
            [.. details]));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Octorate compliance handoff {HandoffId} ({Topic}, {DestinationKind}) started for property {PropertyId} of org {OrgId} by user {UserId}",
            handoffId,
            request.Topic,
            destination.Kind,
            property.Id,
            scope.OrgId,
            actorUserId);

        return new OctorateHandoff(
            handoffId,
            now,
            now + options.Value.Lifetime,
            request.Topic,
            destination,
            returnPath,
            property.Id,
            property.Name,
            mapping.SnapshotName,
            booking?.Id,
            booking is null ? null : BookingCodes.Format(booking.BookingCode),
            period,
            OctorateHandoffGuides.For(request.Topic, booking?.CheckInDate, booking?.CheckOutDate));
    }

    private static NotFoundException TargetNotFound() =>
        new("Octorate handoff target not found")
        {
            Code = TargetNotFoundCode,
            MessageKey = "OctorateHandoffTargetNotFound",
        };
}
