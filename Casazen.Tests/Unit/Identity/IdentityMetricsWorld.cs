using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// The data the tests of the counters and of the export of the pilot run on (E73/F01/US03): an org with two properties (one a collaborator «Solo alcuni» was given, one it was
/// not), a second org with a property of its own, and the stays, identity steps, rows of the policy and rows of the operators the tests seed on them, in memory. Every time is
/// UTC: the period of the tests is November 2026, «now» is the 27th at noon.
/// </summary>
internal sealed class IdentityMetricsWorld
{
    /// <summary>The instant the services run at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The start of the period of the tests (included).</summary>
    public static readonly DateTime From = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The end of the period of the tests (excluded).</summary>
    public static readonly DateTime To = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    public const string Member = "auth0|collaboratore";
    public const string OwnerOfGranted = "auth0|titolare";
    public const string OwnerOfHidden = "auth0|altro-titolare";
    public const string OperatorUser = "auth0|operatore";

    /// <summary>The policy version and hash the rows of the policy carry.</summary>
    public const string PolicyVersion = "identity-policy-2026.11-1";

    public const string PolicyHash = "9f2c4e6a8b0d1f3a5c7e9b1d3f5a7c9e1b3d5f7a9c1e3b5d7f9a1c3e5b7d9f1a";

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"identity-metrics-{Guid.NewGuid()}").Options;

    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly List<object> _rows = [];
    private readonly Dictionary<Guid, Booking> _stays = [];

    public IdentityMetricsWorld()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Agenzia", $"agenzia-{suffix}");
        var other = HostScopeScenario.NewOrg("Altra agenzia", $"altra-{suffix}");
        OrgId = org.Id;
        OtherOrgId = other.Id;
        var granted = HostScopeScenario.NewProperty(org.Id, OwnerOfGranted, "Bilocale Navigli");
        var hidden = HostScopeScenario.NewProperty(org.Id, OwnerOfHidden, "Villa Brera");
        var foreign = HostScopeScenario.NewProperty(other.Id, "auth0|altra", "Trilocale Altrove");
        GrantedPropertyId = granted.Id;
        HiddenPropertyId = hidden.Id;
        ForeignPropertyId = foreign.Id;
        _rows.AddRange([org, other, granted, hidden, foreign]);
        _rows.Add(new PropertyMemberAccess { OrgId = org.Id, UserId = Member, PropertyId = granted.Id });
    }

    public Guid OrgId { get; }

    public Guid OtherOrgId { get; }

    /// <summary>A property of the org the collaborator «Solo alcuni» was given.</summary>
    public Guid GrantedPropertyId { get; }

    /// <summary>A property of the same org the collaborator was not given.</summary>
    public Guid HiddenPropertyId { get; }

    /// <summary>A property of the other org.</summary>
    public Guid ForeignPropertyId { get; }

    /// <summary>The scope of the owner, the administrators and the managers: the whole org.</summary>
    public HostScope OrgWide => new(OrgId);

    /// <summary>The scope of the collaborator «Solo alcuni»: the property it was given.</summary>
    public HostScope Restricted => new(OrgId, GrantedToUserId: Member);

    /// <summary>The scope of an account in no org team: the properties it created.</summary>
    public HostScope OwnedByTheOwnerOfTheHiddenProperty => new(OrgId, OwnerId: OwnerOfHidden);

    /// <summary>A context on the world, with the tenant filter of <paramref name="tenantOrgId"/> when given (the filter is off otherwise, as in a job or a unit test).</summary>
    public AppDbContext NewDb(Guid? tenantOrgId = null) => new(_options, tenantOrgId is { } tenant ? new FixedTenantContext(tenant) : null, _dataProtection);

    /// <summary>A stay of the property with its guest (a guest with a name, an e-mail and a document number the export must never carry); the id of the booking.</summary>
    public Guid AddStay(Guid propertyId, Guid? orgId = null, string firstName = "Ospite", string lastName = "Prova", string? email = null, string documentNumber = "")
    {
        var org = orgId ?? (propertyId == ForeignPropertyId ? OtherOrgId : OrgId);
        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = org,
            FirstName = firstName,
            LastName = lastName,
            Email = email ?? $"{Guid.NewGuid():N}@example.com",
            DocumentNumber = documentNumber,
        };
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = org,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 12, 12, 0, 0, 0, DateTimeKind.Utc),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            NumberOfGuests = 1,
            TotalPrice = 200m,
        };
        _rows.Add(guest);
        _rows.Add(booking);
        _stays[booking.Id] = booking;
        return booking.Id;
    }

    /// <summary>
    /// An identity step of the stay, opened at <paramref name="openedAt"/> and, when <paramref name="closedAt"/> is given, closed with <paramref name="outcomeCode"/> (a code of
    /// <see cref="IdentityOutcomeCodes"/>, or whatever the test wants to read). The session of the org of the stay unless <paramref name="orgId"/> says otherwise.
    /// </summary>
    public GuestCheckInSession AddSession(
        Guid bookingId,
        DateTime? openedAt,
        DateTime? closedAt = null,
        string? outcomeCode = null,
        string? formatId = "it-cie-3.0",
        string? channel = IdentityChannels.IosSafari,
        int guidedRetries = 0,
        Guid? orgId = null)
    {
        var session = new GuestCheckInSession
        {
            Id = Guid.NewGuid(),
            BookingId = bookingId,
            OrgId = orgId ?? _stays[bookingId].OrgId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = Now.UtcDateTime.AddDays(7),
            Status = GuestCheckInSessionStatus.InCompilazione,
            CreatedAt = (openedAt ?? Now.UtcDateTime).AddMinutes(-5),
            IdentityOpenedAt = openedAt,
            IdentityClosedAt = closedAt,
            IdentityOutcomeCode = outcomeCode,
            IdentityFormatId = formatId,
            IdentityChannel = channel,
            IdentityGuidedRetries = guidedRetries,
        };
        _rows.Add(session);
        return session;
    }

    /// <summary>The row the closure of the policy writes: how it was decided, what the policy would have decided, how long the presence took, and the reasons it recorded.</summary>
    public BiometricVerificationLog AddDecision(
        Guid bookingId,
        DateTime at,
        BiometricOutcome outcome,
        BiometricOutcome shadow,
        IdentityDecisionMode mode = IdentityDecisionMode.Observation,
        int? durationMs = 900,
        string? reasonsJson = null,
        string? formatId = "it-cie-3.0",
        string? channel = IdentityChannels.IosSafari,
        Guid? orgId = null)
    {
        var booking = _stays[bookingId];
        var row = new BiometricVerificationLog
        {
            Id = Guid.NewGuid(),
            OrgId = orgId ?? booking.OrgId,
            BookingId = bookingId,
            GuestId = booking.GuestId,
            Outcome = outcome,
            Method = VerificationMethod.Biometric,
            Attempts = 1,
            ModelName = "SFace",
            ModelVersion = "2021dec",
            ThresholdsJson = "{}",
            ReasonsJson = reasonsJson ?? "[]",
            DurationMs = durationMs,
            PolicyVersion = PolicyVersion,
            PolicyHash = PolicyHash,
            DecisionMode = mode,
            ShadowOutcome = shadow,
            FormatId = formatId,
            Channel = channel,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = at,
        };
        _rows.Add(row);
        return row;
    }

    /// <summary>A row of the legacy path (the agent of before the self-hosted verification): no policy, no shadow outcome, no key.</summary>
    public BiometricVerificationLog AddLegacy(Guid bookingId, DateTime at, BiometricOutcome outcome)
    {
        var booking = _stays[bookingId];
        var row = new BiometricVerificationLog
        {
            Id = Guid.NewGuid(),
            OrgId = booking.OrgId,
            BookingId = bookingId,
            GuestId = booking.GuestId,
            Outcome = outcome,
            Method = VerificationMethod.Biometric,
            Attempts = 1,
            ModelName = "SFace",
            ModelVersion = "2021dec",
            ThresholdsJson = "{}",
            DurationMs = 777,
            CreatedAt = at,
        };
        _rows.Add(row);
        return row;
    }

    /// <summary>
    /// A row of a person from the queue: <see cref="BiometricOutcome.Accept"/> an approval, <see cref="BiometricOutcome.Reject"/> a rejection, and
    /// <see cref="BiometricOutcome.Escalate"/> the row that asks the guest for a new photo (not a decision).
    /// </summary>
    public BiometricVerificationLog AddOperator(Guid bookingId, DateTime at, BiometricOutcome outcome, string? note = null, Guid? orgId = null)
    {
        var booking = _stays[bookingId];
        var row = new BiometricVerificationLog
        {
            Id = Guid.NewGuid(),
            OrgId = orgId ?? booking.OrgId,
            BookingId = bookingId,
            GuestId = booking.GuestId,
            Outcome = outcome,
            Method = VerificationMethod.Operator,
            Attempts = 1,
            ModelName = "operator",
            ModelVersion = "n/a",
            ThresholdsJson = "{}",
            OperatorUserId = OperatorUser,
            OperatorNote = note,
            CreatedAt = at,
        };
        _rows.Add(row);
        return row;
    }

    /// <summary>
    /// The window the retention job of the identity verification leaves, built from the data it writes (nothing of the job is used here): the first session of a stay was left open (the
    /// guest opened the step on <c>web:ios-safari</c> at 09:00 and went away, no format read) and the job closed it as <c>abandoned</c> at 10:40, the time of the job, not the time
    /// of the last thing the guest did. Meanwhile the stay got a new link: its second session (<c>web:android-chrome</c>, <c>it-cie-3.0</c>) opened at 10:00, was decided by the policy at
    /// 10:05 (a review it would have approved, 1100 ms) and approved by a person at 10:35. The time of the abandoned session, from 09:00 to 10:40, holds the row of the second one.
    /// Everything happens on the 10th of November 2026; the row of the policy is the only one of the stay and belongs to the second session.
    /// </summary>
    public (GuestCheckInSession Abandoned, GuestCheckInSession Later, BiometricVerificationLog Decision) AddAbandonedBeforeALaterDecision(Guid propertyId)
    {
        var stay = AddStay(propertyId);
        var abandoned = AddSession(stay, At(10, 9), At(10, 10, 40), IdentityOutcomeCodes.Abandoned, formatId: null, channel: IdentityChannels.IosSafari);
        var later = AddSession(stay, At(10, 10), At(10, 10, 5), IdentityOutcomeCodes.Review, channel: IdentityChannels.AndroidChrome);
        var decision = AddDecision(stay, At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Accept, durationMs: 1100, channel: IdentityChannels.AndroidChrome);
        AddOperator(stay, At(10, 10, 35), BiometricOutcome.Accept);
        return (abandoned, later, decision);
    }

    /// <summary>Another row the test builds by hand (a stay guest with a document number, for example).</summary>
    public void Add(object row) => _rows.Add(row);

    public async Task SaveAsync()
    {
        await using var db = NewDb();
        foreach (var row in _rows)
            db.Add(row);

        await db.SaveChangesAsync();
        _rows.Clear();
    }

    /// <summary>A moment of the period: <paramref name="day"/> of November 2026 at <paramref name="hour"/>:<paramref name="minute"/>:<paramref name="second"/> UTC.</summary>
    public static DateTime At(int day, int hour = 10, int minute = 0, int second = 0) => new(2026, 11, day, hour, minute, second, DateTimeKind.Utc);

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
