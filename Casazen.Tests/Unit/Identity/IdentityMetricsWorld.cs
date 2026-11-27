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

internal sealed class IdentityMetricsWorld
{
    public static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    public static readonly DateTime From = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    public static readonly DateTime To = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    public const string Member = "auth0|collaboratore";
    public const string OwnerOfGranted = "auth0|titolare";
    public const string OwnerOfHidden = "auth0|altro-titolare";
    public const string OperatorUser = "auth0|operatore";

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

    public Guid GrantedPropertyId { get; }

    public Guid HiddenPropertyId { get; }

    public Guid ForeignPropertyId { get; }

    public HostScope OrgWide => new(OrgId);

    public HostScope Restricted => new(OrgId, GrantedToUserId: Member);

    public HostScope OwnedByTheOwnerOfTheHiddenProperty => new(OrgId, OwnerId: OwnerOfHidden);

    /// <summary>A context on the world, with the tenant filter of <paramref name="tenantOrgId"/> when given (the filter is off otherwise, as in a job or a unit test).</summary>
    public AppDbContext NewDb(Guid? tenantOrgId = null) => new(_options, tenantOrgId is { } tenant ? new FixedTenantContext(tenant) : null, _dataProtection);

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

    public (GuestCheckInSession Abandoned, GuestCheckInSession Later, BiometricVerificationLog Decision) AddAbandonedBeforeALaterDecision(Guid propertyId)
    {
        var stay = AddStay(propertyId);
        var abandoned = AddSession(stay, At(10, 9), At(10, 10, 40), IdentityOutcomeCodes.Abandoned, formatId: null, channel: IdentityChannels.IosSafari);
        var later = AddSession(stay, At(10, 10), At(10, 10, 5), IdentityOutcomeCodes.Review, channel: IdentityChannels.AndroidChrome);
        var decision = AddDecision(stay, At(10, 10, 5), BiometricOutcome.Escalate, BiometricOutcome.Accept, durationMs: 1100, channel: IdentityChannels.AndroidChrome);
        AddOperator(stay, At(10, 10, 35), BiometricOutcome.Accept);
        return (abandoned, later, decision);
    }

    public void Add(object row) => _rows.Add(row);

    public async Task SaveAsync()
    {
        await using var db = NewDb();
        foreach (var row in _rows)
            db.Add(row);

        await db.SaveChangesAsync();
        _rows.Clear();
    }

    public static DateTime At(int day, int hour = 10, int minute = 0, int second = 0) => new(2026, 11, day, hour, minute, second, DateTimeKind.Utc);

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
