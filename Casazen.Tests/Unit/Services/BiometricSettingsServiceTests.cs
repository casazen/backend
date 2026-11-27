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

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateAsync(_agency, update, "auth0|admin", Ct));
        Assert.Equal(code, refused.Code);
        Assert.Empty(await _db.BiometricSettings.ToListAsync(Ct));
        Assert.Empty(await _db.OrgActivityEntries.ToListAsync(Ct));

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

    [Fact]
    public async Task BiometricSettings_TheBoundsThemselves_AreAccepted()
    {
        var service = Service();

        var widest = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.95m, 0.50m, 1), "auth0|admin", Ct);
        Assert.Equal((0.95m, 0.50m, 1), (widest.AcceptThreshold, widest.EscalateThreshold, widest.MaxAttempts));

        var narrowest = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.5001m, 0.50m, 5), "auth0|admin", Ct);
        Assert.Equal((0.5001m, 0.50m, 5), (narrowest.AcceptThreshold, narrowest.EscalateThreshold, narrowest.MaxAttempts));

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

        var unknown = Guid.NewGuid();
        Assert.Null(await service.MarkCalibratedAsync(unknown, "auth0|staff", Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => service.UpdateAsync(unknown, new BiometricSettingsUpdate(true, 0.90m, 0.60m, 3), "auth0|admin", Ct));
        Assert.DoesNotContain(await _db.BiometricSettings.ToListAsync(Ct), s => s.OrgId == unknown);
        Assert.DoesNotContain(await _db.OrgActivityEntries.ToListAsync(Ct), e => e.OrgId == unknown);

        var admin = new AdminBiometricsController(Mock.Of<IBiometricModelStatusReader>(), service) { ControllerContext = ContextOf("auth0|staff") };
        var notFound = Assert.IsType<ObjectResult>((await admin.MarkCalibrated(unknown, Ct)).Result);
        Assert.Equal(StatusCodes.Status404NotFound, notFound.StatusCode);
        Assert.Equal("not_found", Assert.IsType<ProblemDetails>(notFound.Value).Extensions["code"]);

        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_other);
        var own = new BiometricSettingsController(service, resolver.Object) { ControllerContext = ContextOf("auth0|admin-b") };
        var seen = Assert.IsType<BiometricSettingsDto>(Assert.IsType<OkObjectResult>((await own.Get(Ct)).Result).Value);
        Assert.Equal(0.80m, seen.AcceptThreshold);
        Assert.False(seen.IsDefault);
        foreach (var action in RoutedActions(typeof(BiometricSettingsController)))
        {
            Assert.DoesNotContain(action.Method.GetParameters(), p => p.Name == "orgId" || (p.ParameterType == typeof(Guid) && p.Name != "id"));
            Assert.True(
                action.Verb.Template is null or "segments" or "segments/{id:guid}",
                $"{action.Method.Name} names something in its route: {action.Verb.Template}");
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

        var same = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(true, 0.85m, 0.70m, 3), "auth0|admin", Ct);
        Assert.False(same.Provisional);

        _clock.Advance(TimeSpan.FromMinutes(5));
        var changed = await service.UpdateAsync(_agency, new BiometricSettingsUpdate(null, null, null, 2), "auth0|admin", Ct);
        Assert.True(changed.Provisional);
        Assert.Null(changed.CalibratedAt);
        Assert.Null((await _db.BiometricSettings.AsNoTracking().SingleAsync(Ct)).CalibratedAt);
    }

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

        await service.UpdateAsync(_agency, new BiometricSettingsUpdate(null, 0.85m, 0.70m, 3), "auth0|admin", Ct);
        Assert.Empty(await _db.BiometricSettings.ToListAsync(Ct));
        Assert.Empty(await _db.OrgActivityEntries.ToListAsync(Ct));
    }

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

        Assert.IsType<OkObjectResult>((await controller.Put(new UpdateBiometricSettingsDto(), Ct)).Result);
        await Assert.ThrowsAsync<DomainRuleException>(() => controller.Put(new UpdateBiometricSettingsDto { MaxAttempts = 9 }, Ct));

        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await controller.Get(Ct)).Result).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await controller.Put(new UpdateBiometricSettingsDto(), Ct)).Result).StatusCode);
    }

    [Fact]
    public void Endpoint_IsBehindTheIdentificationFlag()
    {
        var gates = typeof(BiometricSettingsController).GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(g => g.Flag);

        Assert.Equal([FeatureFlags.CheckInIdentityVerification], gates);

        Assert.Equal(
            ["CreateSegment", "Get", "GetSegments", "Put", "UpdateSegment"],
            RoutedActions(typeof(BiometricSettingsController)).Select(a => a.Method.Name).Order(StringComparer.Ordinal));
    }

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

    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
