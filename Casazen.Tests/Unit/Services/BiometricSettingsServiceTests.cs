using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E22/F01/US01/T03: the biometric settings of an organization. An organization administrator sets the thresholds within bounds (0.50 &lt;= escalate &lt; accept
/// &lt;= 0.95, attempts 1 to 5) and every change leaves them provisional; only platform staff marks a calibration; nobody reads or writes another organization's
/// row; and a change leaves a line in the activity log and in the application log. No row is today's defaults.
/// </summary>
public class BiometricSettingsServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly DbContextOptions<AppDbContext> _options =
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(Now);
    private readonly List<string> _logs = [];
    private AppDbContext _db = null!;
    private Guid _agency;
    private Guid _starter;
    private Guid _other;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(_options);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agency = HostScopeScenario.NewOrg("Agenzia", $"agenzia-{suffix}");
        agency.PlanTier = PlanTier.Agency;
        var starter = HostScopeScenario.NewOrg("Piccolo host", $"piccolo-{suffix}");
        starter.PlanTier = PlanTier.Starter;
        var other = HostScopeScenario.NewOrg("Altra agenzia", $"altra-{suffix}");
        other.PlanTier = PlanTier.Agency;
        _db.Orgs.AddRange(agency, starter, other);
        await _db.SaveChangesAsync();
        _agency = agency.Id;
        _starter = starter.Id;
        _other = other.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ─── The proofs of the brief ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): thresholds out of bounds are refused with their own code and nothing changes. The bounds are 0.50 &lt;= escalate &lt; accept &lt;= 0.95 and
    /// attempts 1 to 5, checked on the whole result (a member left out keeps its value), with no row written, no line in the activity log, and the row of an
    /// organization that has one left exactly as it was.
    /// </summary>
    [Theory]
    [InlineData("0.85", "0.49", 3, "biometric_thresholds_out_of_range")]
    [InlineData("0.96", "0.70", 3, "biometric_thresholds_out_of_range")]
    [InlineData("1.20", "0.70", 3, "biometric_thresholds_out_of_range")]
    [InlineData("0.85", "-0.10", 3, "biometric_thresholds_out_of_range")]
    [InlineData("0.70", "0.70", 3, "biometric_thresholds_order_invalid")]
    [InlineData("0.60", "0.70", 3, "biometric_thresholds_order_invalid")]
    [InlineData("0.85", "0.70", 0, "biometric_attempts_out_of_range")]
    [InlineData("0.85", "0.70", 6, "biometric_attempts_out_of_range")]
    [InlineData("0.85", "0.70", -1, "biometric_attempts_out_of_range")]
    public async Task BiometricSettings_OutOfBoundsRefused_Test(string accept, string escalate, int attempts, string code)
    {
        var service = Service();
        var update = new BiometricSettingsUpdate(true, Decimal(accept), Decimal(escalate), attempts);

        // No row yet: refused, and nothing is created or logged.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateAsync(_agency, update, "auth0|admin", Ct));
        Assert.Equal(code, refused.Code);
        Assert.Empty(await _db.BiometricSettings.ToListAsync(Ct));
        Assert.Empty(await _db.OrgActivityEntries.ToListAsync(Ct));

        // With a row: refused, and the row is as it was (the values, the calibration, the single line of its creation).
        await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.80m, 0.60m, 4), "auth0|admin", Ct);
        await service.MarkCalibratedAsync(_agency, "auth0|staff", Ct);
        var before = await _db.BiometricSettings.AsNoTracking().SingleAsync(Ct);
        var lines = await _db.OrgActivityEntries.CountAsync(Ct);

        await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateAsync(_agency, update, "auth0|admin", Ct));

        var after = await _db.BiometricSettings.AsNoTracking().SingleAsync(Ct);
        Assert.Equal((before.Enabled, before.AcceptThreshold, before.EscalateThreshold, before.MaxAttempts, before.CalibratedAt, before.UpdatedAt),
            (after.Enabled, after.AcceptThreshold, after.EscalateThreshold, after.MaxAttempts, after.CalibratedAt, after.UpdatedAt));
        Assert.Equal(lines, await _db.OrgActivityEntries.CountAsync(Ct));
    }

    /// <summary>A member left out keeps its value, and the bounds are checked on the result: a threshold that alone looks fine can break the order against the other.</summary>
    [Fact]
    public async Task BiometricSettings_APartialUpdate_IsCheckedAsAWhole()
    {
        var service = Service();
        await service.UpdateAsync(_agency, new BiometricSettingsUpdate(null, 0.80m, 0.60m, null), "auth0|admin", Ct);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.UpdateAsync(_agency, new BiometricSettingsUpdate(null, 0.55m, null, null), "auth0|admin", Ct));
        Assert.Equal(BiometricSettingsErrors.ThresholdsOrderInvalid, refused.Code);

        var kept = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(null, null, null, 5), "auth0|admin", Ct);
        Assert.Equal((0.80m, 0.60m, 5), (kept.AcceptThreshold, kept.EscalateThreshold, kept.MaxAttempts));
    }

    /// <summary>The bounds themselves are accepted, a value is rounded to the four decimals the column keeps before it is checked, and the same values again are not a change.</summary>
    [Fact]
    public async Task BiometricSettings_TheBoundsThemselves_AreAccepted()
    {
        var service = Service();

        var widest = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.95m, 0.50m, 1), "auth0|admin", Ct);
        Assert.Equal((0.95m, 0.50m, 1), (widest.AcceptThreshold, widest.EscalateThreshold, widest.MaxAttempts));

        var narrowest = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.5001m, 0.50m, 5), "auth0|admin", Ct);
        Assert.Equal((0.5001m, 0.50m, 5), (narrowest.AcceptThreshold, narrowest.EscalateThreshold, narrowest.MaxAttempts));

        // 0.95004 is 0.9500 in the column, so it is inside the bound; 0.49996 is 0.5000.
        var rounded = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.95004m, 0.49996m, 3), "auth0|admin", Ct);
        Assert.Equal((0.95m, 0.50m), (rounded.AcceptThreshold, rounded.EscalateThreshold));
    }

    /// <summary>
    /// Proof (permission): another organization's settings are out of reach. An organization reads its own row or today's defaults, never another's; a write for
    /// one leaves the other's row as it was; the platform's calibration of an organization that does not exist answers nothing (404 at the endpoint) and writes
    /// nothing; and the route of the organization's own endpoint has no organization in it, only the one of the caller's context.
    /// </summary>
    [Fact]
    public async Task BiometricSettings_OtherOrganization404_Test()
    {
        var service = Service();
        await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.90m, 0.60m, 4), "auth0|admin-a", Ct);

        var seenByOther = await service.GetAsync(_other, Ct);
        Assert.True(seenByOther.IsDefault);
        Assert.Equal((0.85m, 0.70m, 3), (seenByOther.AcceptThreshold, seenByOther.EscalateThreshold, seenByOther.MaxAttempts));

        await service.UpdateAsync(_other, new BiometricSettingsUpdate(false, 0.80m, 0.55m, 2), "auth0|admin-b", Ct);
        var rowOfFirst = await _db.BiometricSettings.AsNoTracking().SingleAsync(s => s.OrgId == _agency, Ct);
        Assert.Equal((true, 0.90m, 0.60m, 4), (rowOfFirst.Enabled, rowOfFirst.AcceptThreshold, rowOfFirst.EscalateThreshold, rowOfFirst.MaxAttempts));
        Assert.Equal(0.90m, (await service.GetAsync(_agency, Ct)).AcceptThreshold);
        Assert.Equal(0.80m, (await service.GetAsync(_other, Ct)).AcceptThreshold);

        // An organization that does not exist: nothing is calibrated, written or created.
        var unknown = Guid.NewGuid();
        Assert.Null(await service.MarkCalibratedAsync(unknown, "auth0|staff", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(unknown, new BiometricSettingsUpdate(true, 0.90m, 0.60m, 3), "auth0|admin", Ct));
        Assert.DoesNotContain(await _db.BiometricSettings.ToListAsync(Ct), s => s.OrgId == unknown);
        Assert.DoesNotContain(await _db.OrgActivityEntries.ToListAsync(Ct), e => e.OrgId == unknown);

        // At the endpoint of the platform: 404 not_found.
        var admin = new AdminBiometricsController(Mock.Of<IBiometricModelStatusReader>(), service) { ControllerContext = ContextOf("auth0|staff") };
        var notFound = Assert.IsType<ObjectResult>((await admin.MarkCalibrated(unknown, Ct)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal("not_found", Assert.IsType<ProblemDetails>(notFound.Value).Extensions["code"]);

        // The endpoint of the organization has no organization to name: it works on the one of the caller's context, and only on it.
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_other);
        var own = new BiometricSettingsController(service, resolver.Object) { ControllerContext = ContextOf("auth0|admin-b") };
        var seen = Assert.IsType<BiometricSettingsDto>(Assert.IsType<OkObjectResult>((await own.Get(Ct)).Result).Value);
        Assert.Equal(0.80m, seen.AcceptThreshold);
        Assert.False(seen.IsDefault);
        foreach (var action in RoutedActions(typeof(BiometricSettingsController)))
        {
            Assert.DoesNotContain(action.Method.GetParameters(), p => p.ParameterType == typeof(Guid) || p.Name == "orgId");
            Assert.DoesNotContain("{", action.Verb.Template ?? string.Empty);
        }

        Assert.Equal("api/settings/biometrics", typeof(BiometricSettingsController).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    /// <summary>
    /// Only platform staff marks a calibration. The settings an organization sends have no calibration member, a change by the organization always leaves the
    /// thresholds provisional (a calibration does not outlive the values it was made on), the same values again keep it, and the endpoint that writes it is
    /// under the platform administrators' policy while the organization's endpoint is under its own administrators'.
    /// </summary>
    [Fact]
    public async Task BiometricSettings_AnOrganizationChange_AlwaysLeavesThemProvisional_AndOnlyStaffCalibrates()
    {
        Assert.DoesNotContain(
            typeof(UpdateBiometricSettingsDto).GetProperties().Concat(typeof(BiometricSettingsUpdate).GetProperties()),
            p => p.Name.Contains("Calibrat", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Provisional", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([CasazenPolicies.OrgBillingAdmin], typeof(BiometricSettingsController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        var calibrate = typeof(AdminBiometricsController).GetMethod(nameof(AdminBiometricsController.MarkCalibrated))!;
        Assert.Equal([CasazenPolicies.AdminOnly], typeof(AdminBiometricsController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        Assert.Empty(calibrate.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal("{orgId:guid}/calibrated", calibrate.GetCustomAttribute<HttpPutAttribute>()!.Template);

        var service = Service();
        var first = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.85m, 0.70m, 3), "auth0|admin", Ct);
        Assert.True(first.Provisional);
        Assert.Null(first.CalibratedAt);

        var calibrated = await service.MarkCalibratedAsync(_agency, "auth0|staff", Ct);
        Assert.NotNull(calibrated);
        Assert.False(calibrated.Provisional);
        Assert.Equal(Now.UtcDateTime, calibrated.CalibratedAt);

        // The same values again: no change, the calibration stays.
        var same = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.85m, 0.70m, 3), "auth0|admin", Ct);
        Assert.False(same.Provisional);

        // A different value, even the attempts alone or the switch: provisional again.
        _clock.Advance(TimeSpan.FromMinutes(5));
        var changed = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(null, null, null, 2), "auth0|admin", Ct);
        Assert.True(changed.Provisional);
        Assert.Null(changed.CalibratedAt);
        Assert.Null((await _db.BiometricSettings.AsNoTracking().SingleAsync(Ct)).CalibratedAt);
    }

    // ─── Defaults, audit, the endpoint ───────────────────────────────────────────────────────────────────

    /// <summary>No row is today's defaults: 0.85, 0.70 and 3 attempts, provisional, enabled on the Agency plan only (ADR-013-R13); the same the flow applies.</summary>
    [Fact]
    public async Task BiometricSettings_NoRow_IsTodaysDefaults()
    {
        var service = Service();

        var agency = await service.GetAsync(_agency, Ct);
        var starter = await service.GetAsync(_starter, Ct);

        Assert.True(agency.IsDefault);
        Assert.True(agency.Enabled);
        Assert.False(starter.Enabled);
        Assert.All(new[] { agency, starter }, view =>
        {
            Assert.Equal((0.85m, 0.70m, 3, true, (DateTime?)null), (view.AcceptThreshold, view.EscalateThreshold, view.MaxAttempts, view.Provisional, view.CalibratedAt));
        });
        Assert.Equal(IdentityVerificationFlowService.DefaultPolicy.AcceptThreshold, (double)agency.AcceptThreshold);
        Assert.Equal(IdentityVerificationFlowService.DefaultPolicy.EscalateThreshold, (double)agency.EscalateThreshold);
        Assert.Equal(IdentityVerificationFlowService.DefaultPolicy.MaxAttempts, agency.MaxAttempts);
        Assert.Empty(await _db.BiometricSettings.ToListAsync(Ct));

        // Saving the defaults again is no change: no row, no line.
        await service.UpdateAsync(_agency, new BiometricSettingsUpdate(null, 0.85m, 0.70m, 3), "auth0|admin", Ct);
        Assert.Empty(await _db.BiometricSettings.ToListAsync(Ct));
        Assert.Empty(await _db.OrgActivityEntries.ToListAsync(Ct));
    }

    /// <summary>
    /// A change writes one line in the activity log of the org (the fact, no values, the actor and the org as the subject) in the same save, and one in the
    /// application log with the new numbers and who; the calibration writes one with the staff user as the actor; a repeat writes none.
    /// </summary>
    [Fact]
    public async Task BiometricSettings_AChangeLeavesAnAuditLine_TheCalibrationToo_ARepeatNone()
    {
        var service = Service();

        await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.90m, 0.65m, 4), "auth0|admin", Ct);
        await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.90m, 0.65m, 4), "auth0|admin", Ct);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await service.MarkCalibratedAsync(_agency, "auth0|staff", Ct);

        var lines = await _db.OrgActivityEntries.OrderBy(e => e.When).ToListAsync(Ct);
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.Equal(OrgActivityType.OrgSettingsChanged, line.Type);
            Assert.Equal(_agency, line.OrgId);
            Assert.Equal(_agency.ToString(), line.SubjectId);
            Assert.Equal("{}", line.DetailsJson);
        });
        Assert.Equal(["auth0|admin", "auth0|staff"], lines.Select(l => l.ActorUserId));

        var change = Assert.Single(_logs, line => line.Contains("changed by", StringComparison.Ordinal));
        Assert.Contains("auth0|admin", change, StringComparison.Ordinal);
        Assert.Contains(0.90m.ToString(CultureInfo.CurrentCulture), change, StringComparison.Ordinal);
        Assert.Contains("provisional", change, StringComparison.Ordinal);
        Assert.Single(_logs, line => line.Contains("marked as calibrated by auth0|staff", StringComparison.Ordinal));
    }

    /// <summary>The staff's calibration of an organization that never saved anything gives it a row with today's defaults (enabled as its plan says), calibrated.</summary>
    [Fact]
    public async Task BiometricSettings_CalibratingAnOrganizationWithNoRow_CreatesItsRowFromTheDefaults()
    {
        var service = Service();

        var agency = await service.MarkCalibratedAsync(_agency, "auth0|staff", Ct);
        var starter = await service.MarkCalibratedAsync(_starter, "auth0|staff", Ct);

        Assert.NotNull(agency);
        Assert.NotNull(starter);
        Assert.True(agency.Enabled);
        Assert.False(starter.Enabled);
        Assert.All(new[] { agency, starter }, view =>
        {
            Assert.Equal((0.85m, 0.70m, 3, false, false), (view.AcceptThreshold, view.EscalateThreshold, view.MaxAttempts, view.Provisional, view.IsDefault));
            Assert.Equal(Now.UtcDateTime, view.CalibratedAt);
        });
        Assert.Equal(2, await _db.BiometricSettings.CountAsync(Ct));
    }

    [Fact]
    public async Task Endpoint_GetAndPut_WorkOnTheOrganizationOfTheCaller_AndAnswerTheViewAndTheErrors()
    {
        var service = Service();
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_agency);
        var controller = new BiometricSettingsController(service, resolver.Object) { ControllerContext = ContextOf("auth0|admin") };

        var defaults = Assert.IsType<BiometricSettingsDto>(Assert.IsType<OkObjectResult>((await controller.Get(Ct)).Result).Value);
        Assert.True(defaults.IsDefault);
        Assert.True(defaults.Provisional);

        var saved = Assert.IsType<BiometricSettingsDto>(
            Assert.IsType<OkObjectResult>((await controller.Put(new UpdateBiometricSettingsDto { Enabled = true, AcceptThreshold = 0.9m, EscalateThreshold = 0.6m, MaxAttempts = 2 }, Ct)).Result).Value);
        Assert.Equal((true, 0.9m, 0.6m, 2, true, false), (saved.Enabled, saved.AcceptThreshold, saved.EscalateThreshold, saved.MaxAttempts, saved.Provisional, saved.IsDefault));
        Assert.Equal("auth0|admin", (await _db.OrgActivityEntries.SingleAsync(Ct)).ActorUserId);

        // An empty body changes nothing; a bound that is broken is the service's DomainRuleException (the error middleware makes it a 422).
        Assert.IsType<OkObjectResult>((await controller.Put(new UpdateBiometricSettingsDto(), Ct)).Result);
        await Assert.ThrowsAsync<DomainRuleException>(() => controller.Put(new UpdateBiometricSettingsDto { MaxAttempts = 9 }, Ct));

        // No organization in the context: 404.
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await controller.Get(Ct)).Result).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await controller.Put(new UpdateBiometricSettingsDto(), Ct)).Result).StatusCode);
    }

    /// <summary>Behind the flag of the identification, like the rest of it: while it is off the settings answer as a route that does not exist.</summary>
    [Fact]
    public void Endpoint_IsBehindTheIdentificationFlag()
    {
        var gates = typeof(BiometricSettingsController).GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag);

        Assert.Equal([FeatureFlags.CheckInIdentityVerification], gates);
        Assert.Equal(["Get", "Put"], RoutedActions(typeof(BiometricSettingsController)).Select(a => a.Method.Name).Order());
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    private BiometricSettingsService Service() => new(_db, _clock, new ListLogger<BiometricSettingsService>(_logs), new ActivityLog(_db, _clock));

    private static decimal Decimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);

    private static List<(MethodInfo Method, HttpMethodAttribute Verb)> RoutedActions(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Verb: m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).FirstOrDefault()))
            .Where(a => a.Verb is not null)
            .Select(a => (a.Method, a.Verb!))
            .ToList();

    private static ControllerContext ContextOf(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        },
    };

    /// <summary>A logger that keeps every formatted line, to prove what is written.</summary>
    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
