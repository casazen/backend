using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// The edge of the bulk configuration (NF-105, E10/F02/US01): what the two controllers do with a body the client left out, with a file it did not
/// send or sent too large, with a flow that does not exist and with a caller that has no scope. Everything a client leaves out is read as «no», never
/// as «all»: nothing is written unless the body says <c>dryRun</c> false AND <c>confirmed</c> true, and the import checks the file unless it says
/// <c>dryRun=false</c>. The rules behind it are in <c>OnboardingBulkServiceTests</c> and <c>OnboardingBulkImportServiceTests</c>.
/// </summary>
public class OnboardingBulkControllersTests
{
    private const string UserId = "auth0|owner";

    private static readonly Guid OrgId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly Mock<IOnboardingBulkService> _bulk = new();
    private readonly Mock<IOnboardingBulkImportService> _import = new();
    private readonly Mock<IOrgContextResolver> _orgs = new();
    private readonly Mock<IHostScopeResolver> _scopes = new();
    private readonly HostScope _scope = new(OrgId);

    // ---------------------------------------------------------------- the choice and the confirmation

    /// <summary>
    /// Proof (data): an apply writes nothing unless the body says <c>dryRun</c> false AND <c>confirmed</c> true, and both are false when the body
    /// leaves them out (no body, <c>{}</c>, a body without the confirmation). The service then judges and writes nothing
    /// (<c>Apply_DryRunOrNotConfirmed_WritesNothing_AndReportsTheRuleOfEachProperty</c>); a body that names no selection or no choice is refused
    /// there as invalid, never read as «every property» or «the default sender».
    /// </summary>
    [Fact]
    public async Task Apply_WhatTheBodyLeavesOut_IsFalse_SoNothingIsWrittenUnlessItSaysSo()
    {
        _bulk
            .Setup(b => b.ApplyAsync(
                It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<BulkSelection>(), It.IsAny<BulkChoice>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkApplyResult(true, ComplianceSenderType.Manual, new BulkApplySummary(0, 0, 0, 0), []));
        var controller = BulkController();
        const string Rest = """{"selection":{"kind":"all"},"choice":{"complianceSender":"Manual"}""";

        var empty = Read<BulkApplyRequestDto>("{}");
        Assert.Equal((false, false), (empty.DryRun, empty.Confirmed));
        foreach (var body in new[] { null, empty, Read<BulkApplyRequestDto>(Rest + ",\"dryRun\":false}") })
            Assert.IsType<OkObjectResult>((await controller.Apply(body, Ct)).Result);

        // Nothing named: no selection (an empty kind, no ids, no filter) and no choice. The service refuses it.
        _bulk.Verify(
            b => b.ApplyAsync(_scope, UserId, It.Is<BulkSelection>(s => s.Kind == string.Empty && s.PropertyIds == null && s.Filter == null), It.Is<BulkChoice>(c => c.ComplianceSender == null), false, false, Ct),
            Times.Exactly(2));
        _bulk.Verify(
            b => b.ApplyAsync(_scope, UserId, It.Is<BulkSelection>(s => s.Kind == "all"), It.Is<BulkChoice>(c => c.ComplianceSender == ComplianceSenderType.Manual), false, false, Ct),
            Times.Once);

        // Only a body that says so asks for the confirmation, and a dry run is passed on as it is.
        await controller.Apply(Read<BulkApplyRequestDto>(Rest + ",\"confirmed\":true}"), Ct);
        await controller.Apply(Read<BulkApplyRequestDto>(Rest + ",\"dryRun\":true,\"confirmed\":true}"), Ct);
        _bulk.Verify(b => b.ApplyAsync(_scope, UserId, It.IsAny<BulkSelection>(), It.IsAny<BulkChoice>(), false, true, Ct), Times.Once);
        _bulk.Verify(b => b.ApplyAsync(_scope, UserId, It.IsAny<BulkSelection>(), It.IsAny<BulkChoice>(), true, true, Ct), Times.Once);
    }

    /// <summary>An empty body names no selection: it is passed on empty (and refused as invalid by the service), never read as «all».</summary>
    [Fact]
    public async Task Preview_AndPilot_AnEmptyBody_IsNeverAllAndNeverNone()
    {
        _bulk
            .Setup(b => b.PreviewAsync(It.IsAny<HostScope>(), It.IsAny<BulkSelection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkPreview(0, [], 0, false, 500));
        _bulk
            .Setup(b => b.SetPilotAsync(It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkPilot([], 0, 5, 10, true, []));
        var controller = BulkController();

        await controller.Preview(Read<BulkSelectionDto>("{}"), Ct);
        await controller.Preview(null, Ct);
        _bulk.Verify(
            b => b.PreviewAsync(_scope, It.Is<BulkSelection>(s => s.Kind == string.Empty && s.PropertyIds == null && s.Filter == null), Ct),
            Times.Exactly(2));

        // The pilot group: no list is an empty list (422 in the service), not «clear the group» and not «every property».
        await controller.SetPilot(Read<BulkPilotRequestDto>("{}"), Ct);
        await controller.SetPilot(null, Ct);
        _bulk.Verify(b => b.SetPilotAsync(_scope, UserId, It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 0), Ct), Times.Exactly(2));
    }

    /// <summary>
    /// A flow that does not exist is 400 <c>onboarding_flow_unknown</c> and nothing is activated, not even the flows of the same list that do exist;
    /// the names are the members of <see cref="OnboardingFlow"/> in any case. A body with no cohort and no flows is passed on empty (the service
    /// answers 422 for each, it never means «all»).
    /// </summary>
    [Fact]
    public async Task Activate_AnUnknownFlow_Is400_AndNothingIsActivated()
    {
        _bulk
            .Setup(b => b.ActivateAsync(It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<OnboardingFlow>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkActivationResult(BulkCohorts.Pilot, []));
        var controller = BulkController();

        var unknown = await controller.Activate(new BulkActivateRequestDto { Cohort = "pilot", Flows = ["Listings", "Nonsense"] }, Ct);
        Assert.Equal("onboarding_flow_unknown", AssertProblem(unknown.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
        var numeric = await controller.Activate(new BulkActivateRequestDto { Cohort = "pilot", Flows = ["2"] }, Ct);
        AssertProblem(numeric.Result, StatusCodes.Status400BadRequest);
        _bulk.Verify(
            b => b.ActivateAsync(It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<OnboardingFlow>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        Assert.IsType<OkObjectResult>((await controller.Activate(new BulkActivateRequestDto { Cohort = "rest", Flows = ["listings", "Bookings"] }, Ct)).Result);
        _bulk.Verify(
            b => b.ActivateAsync(_scope, UserId, "rest", It.Is<IReadOnlyCollection<OnboardingFlow>>(f => f.SequenceEqual(new[] { OnboardingFlow.Listings, OnboardingFlow.Bookings })), Ct),
            Times.Once);

        Assert.IsType<OkObjectResult>((await controller.Activate(Read<BulkActivateRequestDto>("{}"), Ct)).Result);
        _bulk.Verify(b => b.ActivateAsync(_scope, UserId, string.Empty, It.Is<IReadOnlyCollection<OnboardingFlow>>(f => f.Count == 0), Ct), Times.Once);
    }

    /// <summary>A caller with no scope (a deactivated member, another org's) reaches nothing: 403 for every action, and the service is never called.</summary>
    [Fact]
    public async Task ACallerWithNoScope_IsRefused403_AndNothingIsCalled()
    {
        var bulk = BulkController(hasScope: false);
        var import = ImportController(hasScope: false);

        Assert.Equal(ProblemCodes.Forbidden, AssertProblem((await bulk.Preview(Read<BulkSelectionDto>("{}"), Ct)).Result, StatusCodes.Status403Forbidden).Extensions["code"]);
        AssertProblem((await bulk.Apply(Read<BulkApplyRequestDto>("{}"), Ct)).Result, StatusCodes.Status403Forbidden);
        AssertProblem((await bulk.GetProgress(Ct)).Result, StatusCodes.Status403Forbidden);
        AssertProblem((await bulk.GetPilot(Ct)).Result, StatusCodes.Status403Forbidden);
        AssertProblem((await bulk.SetPilot(Read<BulkPilotRequestDto>("{}"), Ct)).Result, StatusCodes.Status403Forbidden);
        AssertProblem((await bulk.Activate(Read<BulkActivateRequestDto>("{}"), Ct)).Result, StatusCodes.Status403Forbidden);
        AssertProblem((await import.Import(Upload("x.csv", "propertyId,lockCode"), true, Ct)).Result, StatusCodes.Status403Forbidden);

        _bulk.VerifyNoOtherCalls();
        _import.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- the file

    /// <summary>
    /// Proof (data): the import checks the file and writes nothing unless the call says <c>dryRun=false</c>: the default of the query is true, so a
    /// call with no <c>dryRun</c> is a check, and <c>false</c> is passed on as it is.
    /// </summary>
    [Fact]
    public async Task Import_ChecksTheFileUnlessTheCallSaysDryRunFalse()
    {
        _import
            .Setup(i => i.ImportAsync(It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkImportResult(true, new BulkImportSummary(0, 0, 0, 0), [], []));
        var controller = ImportController();

        var dryRun = typeof(OnboardingBulkImportController).GetMethod(nameof(OnboardingBulkImportController.Import))!
            .GetParameters().Single(p => p.Name == "dryRun");
        Assert.Equal(true, dryRun.DefaultValue);
        Assert.NotNull(dryRun.GetCustomAttribute<FromQueryAttribute>());

        // The call as the binder makes it when the query has no dryRun.
        var content = "propertyId,lockCode\r\n"u8.ToArray();
        Assert.IsType<OkObjectResult>((await controller.Import(Upload("x.csv", content))).Result);
        _import.Verify(i => i.ImportAsync(_scope, UserId, "x.csv", It.Is<byte[]>(b => b.SequenceEqual(content)), true, It.IsAny<CancellationToken>()), Times.Once);

        // Written for real only when the call says so.
        await controller.Import(Upload("x.csv", content), dryRun: false, Ct);
        _import.Verify(i => i.ImportAsync(_scope, UserId, "x.csv", It.IsAny<byte[]>(), false, Ct), Times.Once);
    }

    /// <summary>No file in the field <c>file</c> (a multipart body without it) or an empty one: 400 <c>import_file_missing</c>, and nothing is read.</summary>
    [Fact]
    public async Task Import_NoFile_Is400ImportFileMissing()
    {
        var controller = ImportController();

        var none = await controller.Import(null);
        Assert.Equal("import_file_missing", AssertProblem(none.Result, StatusCodes.Status400BadRequest).Extensions["code"]);
        var empty = await controller.Import(Upload("x.csv", []));
        Assert.Equal("import_file_missing", AssertProblem(empty.Result, StatusCodes.Status400BadRequest).Extensions["code"]);

        _import.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A file whose declared size is over 2 MB is 413 <c>import_file_too_large</c> before a byte of it is copied (the stream is never opened);
    /// exactly 2 MB goes on to the service.
    /// </summary>
    [Fact]
    public async Task Import_AFileOverTheDeclaredLimit_Is413_BeforeAByteIsRead()
    {
        var big = new Mock<IFormFile>(MockBehavior.Strict);
        big.SetupGet(f => f.Length).Returns(BulkImportLimits.MaxFileBytes + 1L);
        var controller = ImportController();

        var refused = await controller.Import(big.Object);

        Assert.Equal("import_file_too_large", AssertProblem(refused.Result, StatusCodes.Status413PayloadTooLarge).Extensions["code"]);
        big.VerifyGet(f => f.Length, Times.AtLeastOnce);
        big.VerifyNoOtherCalls();
        _import.VerifyNoOtherCalls();

        // The limit itself is allowed; it is the service that reads the file.
        _import
            .Setup(i => i.ImportAsync(It.IsAny<HostScope>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkImportResult(true, new BulkImportSummary(0, 0, 0, 0), [], []));
        var atTheLimit = await controller.Import(Upload("x.csv", new byte[BulkImportLimits.MaxFileBytes]));
        Assert.IsType<OkObjectResult>(atTheLimit.Result);
    }

    // ---------------------------------------------------------------- the policy and the flag

    /// <summary>
    /// Proof (permission): the seven actions of the two controllers are under the org policy <see cref="CasazenPolicies.OrgBillingAdmin"/> and
    /// behind <c>Features:OctorateOnboarding</c> (404 before the authorization while it is off), none is anonymous, and the controllers have no
    /// other routed action. The policy itself is proved over the real pipeline for every <c>OrgBillingAdmin</c> action by
    /// <c>OrgBillingAdminMembershipAccessTests</c>.
    /// </summary>
    [Fact]
    public void EveryAction_IsUnderOrgBillingAdmin_AndBehindTheModuleFlag()
    {
        Type[] controllers = [typeof(OnboardingBulkController), typeof(OnboardingBulkImportController)];
        var actions = OrgBillingAdminActions.All.Where(a => controllers.Contains(a.Controller)).ToList();
        var routed = controllers
            .SelectMany(c => c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().Any())
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}");

        Assert.Equal(7, actions.Count);
        Assert.Equal(routed.Order(StringComparer.Ordinal), actions.Select(a => a.Key).Order(StringComparer.Ordinal));
        Assert.All(actions, action =>
        {
            Assert.False(action.IsAnonymous);
            Assert.Equal([CasazenPolicies.OrgBillingAdmin], action.Policies);
            Assert.Equal([FeatureFlags.OctorateOnboarding], action.FeatureGates);
        });
    }

    // ---------------------------------------------------------------- the world

    private OnboardingBulkController BulkController(bool hasScope = true)
    {
        Wire(hasScope);
        return new OnboardingBulkController(_bulk.Object, _orgs.Object, _scopes.Object) { ControllerContext = Context() };
    }

    private OnboardingBulkImportController ImportController(bool hasScope = true)
    {
        Wire(hasScope);
        return new OnboardingBulkImportController(_import.Object, _orgs.Object, _scopes.Object) { ControllerContext = Context() };
    }

    private void Wire(bool hasScope)
    {
        _orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)OrgId);
        _scopes
            .Setup(s => s.ResolveAsync(UserId, It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasScope ? _scope : null);
    }

    private static ControllerContext Context() => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId)], "TestAuth")),
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        },
    };

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;

    private static IFormFile Upload(string name, string content) => Upload(name, System.Text.Encoding.UTF8.GetBytes(content));

    private static IFormFile Upload(string name, byte[] content) => new FormFile(new MemoryStream(content), 0, content.Length, "file", name);

    private static ProblemDetails AssertProblem(IActionResult? result, int status)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        return Assert.IsType<ProblemDetails>(objectResult.Value);
    }
}
