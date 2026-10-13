using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IActivityLog" />
/// <remarks>
/// <para>It adds the line to the <see cref="AppDbContext"/> of the request, the very instance the calling service saves with: the
/// line goes to the database in the <c>SaveChanges</c> of the change it records, in its transaction, or not at all. It never
/// saves and never opens a transaction of its own.</para>
/// <para><b>Dormant while <c>Features:OrgTeam</c> is off.</b> The log is part of the org team (the wave's decision D17 and the
/// privacy notice it needs are the legal advisor's, <c>docs/runbooks/org-team.md</c>): until the flag is turned on a deploy
/// collects nothing, so the plan, the name or the slug of an org change exactly as before. The event is still checked against
/// the catalog, so a mistake in a service shows up in its tests whatever the flag says.</para>
/// <para>The instant is the clock's, to the microsecond: PostgreSQL keeps no more, so what is read back equals what was
/// written.</para>
/// </remarks>
public sealed class ActivityLog(AppDbContext db, IFeatureFlags featureFlags, TimeProvider? timeProvider = null) : IActivityLog
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public void Record(OrgActivity activity)
    {
        OrgActivityCatalog.Validate(activity);
        if (!featureFlags.IsEnabled(FeatureFlags.OrgTeam))
            return;

        var info = OrgActivityCatalog.Describe(activity.Type);

        db.OrgActivityEntries.Add(new OrgActivityEntry
        {
            OrgId = activity.OrgId,
            When = ToMicroseconds(_clock.GetUtcNow().UtcDateTime),
            ActorUserId = activity.ActorUserId,
            Area = activity.Area ?? info.DefaultArea,
            Type = activity.Type,
            SubjectType = info.SubjectType,
            SubjectId = activity.SubjectId,
            DetailsJson = OrgActivityDetails.Serialize(activity.Details),
        });
    }

    /// <summary>.NET counts in 100 ns, PostgreSQL in microseconds: truncated here, a round trip does not change the value.</summary>
    internal static DateTime ToMicroseconds(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % (TimeSpan.TicksPerMillisecond / 1000)), DateTimeKind.Utc);
}
