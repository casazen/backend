using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Admin;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E70/F02/US01/T02 (NF-194, AC5, AC6): the endpoints of the segments. The administrators of an organization read, create and set the mode of the segments of their own
/// organization only (another organization's segment is a uniform 404), the platform administrators alone qualify and revoke, the routes are behind the self-hosted flow,
/// and every message is in Italian and English.
/// </summary>
public sealed class IdentitySegmentEndpointsTests : IAsyncLifetime
{
    private const string Cie = "it-cie-3.0";
    private const string ReportHash = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"identity-segment-endpoints-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly TestFeatureFlags _flags = new(
        FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityAutoApproval);

    private AppDbContext _db = null!;
    private IdentitySegmentService _service = null!;
    private IdentityFingerprint _fingerprint = null!;
    private Guid _org;
    private Guid _other;
    private Guid _flat;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var text = IdentityPolicyTestFiles.Edited(("version", "\"identity-policy-2026.12-1\""), ("provisional", "false"));
        File.WriteAllText(Path.Combine(_directory, "calibrated.json"), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var policy = new IdentityPolicyProvider(
            Options.Create(new IdentityVerificationOptions { PolicyFile = "calibrated.json", PolicySha256 = IdentityPolicyProvider.CanonicalSha256(Encoding.UTF8.GetBytes(text))! }),
            NullLogger<IdentityPolicyProvider>.Instance,
            _directory);
        var fingerprints = new IdentityConfigurationFingerprint(policy, new LoadedModels());
        _fingerprint = fingerprints.Current()!;

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var org = HostScopeScenario.NewOrg("Agenzia", $"agenzia-{suffix}");
        var other = HostScopeScenario.NewOrg("Altra agenzia", $"altra-{suffix}");
        var flat = HostScopeScenario.NewProperty(org.Id, "auth0|owner", "Bilocale Navigli");
        _db.Orgs.AddRange(org, other);
        _db.Properties.Add(flat);
        await _db.SaveChangesAsync(Ct);
        (_org, _other, _flat) = (org.Id, other.Id, flat.Id);

        _service = new IdentitySegmentService(
            _db,
            policy,
            fingerprints,
            new DocumentFormatRegistry(Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, AppContext.BaseDirectory),
            _flags,
            _clock,
            NullLogger<IdentitySegmentService>.Instance,
            new ActivityLog(_db, _clock));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>
    /// Proof (permission): tenant isolation. The administrator of another organization gets 404 <c>not_found</c> on every segment endpoint for a segment of this one (the
    /// change of its mode, the list does not show it), and so does the platform administrator who names the wrong organization in the route; the segment is untouched.
    /// </summary>
    [Fact]
    public async Task IdentitySegmentEndpoints_OtherOrgSegment_Returns404()
    {
        var segment = Assert.IsType<IdentitySegmentDto>(Assert.IsType<OkObjectResult>(
            (await OrgController(_org).CreateSegment(new CreateIdentitySegmentDto { PropertyId = _flat, FormatId = Cie, Channel = IdentityChannels.IosSafari }, _service, Ct)).Result).Value);
        var before = await _db.IdentitySegmentSettings.AsNoTracking().SingleAsync(Ct);

        var intruder = OrgController(_other, "auth0|other-admin");
        foreach (var mode in IdentitySegmentModeCodes.All)
            AssertNotFound((await intruder.UpdateSegment(segment.Id, new UpdateIdentitySegmentDto { Mode = mode, Reason = "Prova" }, _service, Ct)).Result);

        var seenByIntruder = Assert.IsType<IdentitySegmentsDto>(Assert.IsType<OkObjectResult>((await intruder.GetSegments(_service, Ct)).Result).Value);
        Assert.Empty(seenByIntruder.Segments);

        var staff = AdminController();
        AssertNotFound((await staff.QualifySegment(_other, segment.Id, Qualification(), _service, Ct)).Result);
        AssertNotFound((await staff.RevokeSegmentQualification(_other, segment.Id, _service, Ct)).Result);
        AssertNotFound((await OrgController(_org).UpdateSegment(Guid.NewGuid(), new UpdateIdentitySegmentDto { Mode = "observation" }, _service, Ct)).Result);

        Assert.Equivalent(before, await _db.IdentitySegmentSettings.AsNoTracking().SingleAsync(Ct));
        Assert.DoesNotContain(await _db.OrgActivityEntries.ToListAsync(Ct), entry => entry.OrgId == _other);
    }

    /// <summary>
    /// Proof (permission): only the platform administrators qualify or revoke. The two routes are on the controller of <see cref="CasazenPolicies.AdminOnly"/>, nothing opens
    /// them wider, and that policy refuses the owner, a manager and any other role of a host while the platform administrator passes; the endpoints of the organization have
    /// no route and no member that writes a qualification, and their <c>automatic</c> on an unqualified segment is refused.
    /// </summary>
    [Fact]
    public async Task IdentitySegmentEndpoints_QualifyRequiresAdminOnly()
    {
        var qualify = typeof(AdminBiometricsController).GetMethod(nameof(AdminBiometricsController.QualifySegment))!;
        var revoke = typeof(AdminBiometricsController).GetMethod(nameof(AdminBiometricsController.RevokeSegmentQualification))!;
        Assert.Equal([CasazenPolicies.AdminOnly], typeof(AdminBiometricsController).GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        foreach (var action in new[] { qualify, revoke })
        {
            Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
            Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        }

        Assert.Equal("{orgId:guid}/segments/{segmentId:guid}/qualification", qualify.GetCustomAttribute<HttpPutAttribute>()!.Template);
        Assert.Equal("{orgId:guid}/segments/{segmentId:guid}/qualification", revoke.GetCustomAttribute<HttpDeleteAttribute>()!.Template);

        // The organization's own endpoints: no route of a qualification, no member of one.
        foreach (var action in RoutedActions(typeof(BiometricSettingsController)))
            Assert.DoesNotContain("qualif", action.Verb.Template ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(UpdateIdentitySegmentDto).GetProperties().Concat(typeof(CreateIdentitySegmentDto).GetProperties()),
            property => property.Name.Contains("Qualif", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase));

        // The real AdminOnly policy: every host role is refused, the platform administrator passes.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthorization();
        await using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(CasazenPolicies.AdminOnly);
        var requirement = Assert.Single(policy!.Requirements.OfType<RolesAuthorizationRequirement>());
        foreach (var role in new[] { "PropertyOwner", "PropertyManager", "LongTermLandlord", "Staff", "Supplier", "Guest" })
        {
            var asHost = new AuthorizationHandlerContext(policy.Requirements, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test")), resource: null);
            await ((IAuthorizationHandler)requirement).HandleAsync(asHost);
            Assert.False(asHost.HasSucceeded, $"A caller with the role {role} passed the policy of the qualification.");
        }

        var asAdmin = new AuthorizationHandlerContext(policy.Requirements, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test")), resource: null);
        await ((IAuthorizationHandler)requirement).HandleAsync(asAdmin);
        Assert.True(asAdmin.HasSucceeded);

        // What an organization administrator can do: create, but not make automatic what nobody qualified.
        var created = Assert.IsType<IdentitySegmentDto>(Assert.IsType<OkObjectResult>(
            (await OrgController(_org).CreateSegment(new CreateIdentitySegmentDto { FormatId = Cie, Channel = IdentityChannels.AndroidChrome }, _service, Ct)).Result).Value);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => OrgController(_org).UpdateSegment(created.Id, new UpdateIdentitySegmentDto { Mode = "automatic" }, _service, Ct));
        Assert.Equal(IdentityApiErrors.IdentitySegmentNotQualified, refused.Code);

        // The platform administrator qualifies; then the organization may set automatic.
        var qualified = Assert.IsType<IdentitySegmentDto>(Assert.IsType<OkObjectResult>((await AdminController().QualifySegment(_org, created.Id, Qualification(), _service, Ct)).Result).Value);
        Assert.True(qualified.Qualified);
        Assert.Equal(IdentitySegmentModeCodes.Observation, qualified.Mode);
        var automatic = Assert.IsType<IdentitySegmentDto>(Assert.IsType<OkObjectResult>(
            (await OrgController(_org).UpdateSegment(created.Id, new UpdateIdentitySegmentDto { Mode = "automatic" }, _service, Ct)).Result).Value);
        Assert.Equal(IdentitySegmentModeCodes.Automatic, automatic.Mode);
        Assert.True(automatic.AutoApprovalAllowed);
        Assert.Null(automatic.AutoApprovalReason);

        // Revoked by the platform administrator: back to observation.
        var revoked = Assert.IsType<IdentitySegmentDto>(Assert.IsType<OkObjectResult>((await AdminController().RevokeSegmentQualification(_org, created.Id, _service, Ct)).Result).Value);
        Assert.Equal((IdentitySegmentModeCodes.Observation, false), (revoked.Mode, revoked.Qualified));
    }

    /// <summary>The list answers the contract of the page, in camel case: the segments, the formats, the channels, the policy in force and the platform permission.</summary>
    [Fact]
    public async Task IdentitySegmentEndpoints_TheList_AnswersTheContract()
    {
        await OrgController(_org).CreateSegment(new CreateIdentitySegmentDto { PropertyId = _flat, FormatId = Cie, Channel = IdentityChannels.IosSafari }, _service, Ct);

        var list = Assert.IsType<IdentitySegmentsDto>(Assert.IsType<OkObjectResult>((await OrgController(_org).GetSegments(_service, Ct)).Result).Value);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(list, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        Assert.Equal(["activePolicy", "autoApprovalEnabled", "channels", "formats", "segments"], root.EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        var segment = Assert.Single(root.GetProperty("segments").EnumerateArray());
        foreach (var member in new[]
                 {
                     "id", "propertyId", "propertyName", "formatId", "channel", "mode", "qualified", "qualificationReason", "qualifiedPolicyVersion", "qualifiedAt",
                     "autoApprovalAllowed", "requiredAssurance", "suspendedReason",
                 })
        {
            Assert.True(segment.TryGetProperty(member, out _), $"The segment of the list has no member {member}.");
        }

        Assert.Equal("observation", segment.GetProperty("mode").GetString());
        Assert.Equal("Bilocale Navigli", segment.GetProperty("propertyName").GetString());
        Assert.Equal("segment_not_qualified", segment.GetProperty("qualificationReason").GetString());
        Assert.Equal(IdentityChannels.All, root.GetProperty("channels").EnumerateArray().Select(channel => channel.GetString()!).ToList());
        Assert.Contains(root.GetProperty("formats").EnumerateArray(), format => format.GetProperty("id").GetString() == Cie && format.GetProperty("label").GetString()!.Length > 0);
        Assert.Equal("identity-policy-2026.12-1", root.GetProperty("activePolicy").GetProperty("version").GetString());
        Assert.False(root.GetProperty("activePolicy").GetProperty("provisional").GetBoolean());
        Assert.True(root.GetProperty("autoApprovalEnabled").GetBoolean());
    }

    [Fact]
    public async Task IdentitySegmentEndpoints_TheModeIsACodeOfTheList_AndASuspensionKeepsItsReason()
    {
        var segment = Assert.IsType<IdentitySegmentDto>(Assert.IsType<OkObjectResult>(
            (await OrgController(_org).CreateSegment(new CreateIdentitySegmentDto { FormatId = Cie, Channel = IdentityChannels.DesktopFirefox }, _service, Ct)).Result).Value);

        foreach (var mode in new[] { null, "", "auto", "approved" })
        {
            var invalid = Assert.IsType<ObjectResult>((await OrgController(_org).UpdateSegment(segment.Id, new UpdateIdentitySegmentDto { Mode = mode }, _service, Ct)).Result);
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, invalid.StatusCode);
            Assert.Equal(IdentityApiErrors.IdentitySegmentInvalid, Assert.IsType<ProblemDetails>(invalid.Value).Extensions["code"]);
        }

        var suspended = Assert.IsType<IdentitySegmentDto>(Assert.IsType<OkObjectResult>(
            (await OrgController(_org).UpdateSegment(segment.Id, new UpdateIdentitySegmentDto { Mode = "Suspended", Reason = "Falsi rifiuti sopra la soglia" }, _service, Ct)).Result).Value);
        Assert.Equal((IdentitySegmentModeCodes.Suspended, "Falsi rifiuti sopra la soglia"), (suspended.Mode, suspended.SuspendedReason));
        Assert.Equal("auth0|admin", (await _db.IdentitySegmentSettings.AsNoTracking().SingleAsync(Ct)).UpdatedByUserId);

        // No organization in the context: 404, like the settings.
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Guid?)null);
        var nobody = new BiometricSettingsController(Mock.Of<IBiometricSettingsService>(), resolver.Object) { ControllerContext = ContextOf("auth0|admin") };
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await nobody.GetSegments(_service, Ct)).Result).StatusCode);
    }

    /// <summary>Behind the flags: the routes of the organization behind the self-hosted flow (the controller behind the identification), the routes of the staff behind both.</summary>
    [Fact]
    public void IdentitySegmentEndpoints_AreBehindTheSelfHostedFlow()
    {
        Assert.Equal([FeatureFlags.CheckInIdentityVerification], typeof(BiometricSettingsController).GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(gate => gate.Flag));
        foreach (var name in new[] { nameof(BiometricSettingsController.GetSegments), nameof(BiometricSettingsController.CreateSegment), nameof(BiometricSettingsController.UpdateSegment) })
        {
            var gates = typeof(BiometricSettingsController).GetMethod(name)!.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(gate => gate.Flag);
            Assert.Equal([FeatureFlags.CheckInIdentitySelfHosted], gates);
        }

        string[] both = [FeatureFlags.CheckInIdentitySelfHosted, FeatureFlags.CheckInIdentityVerification];
        foreach (var name in new[] { nameof(AdminBiometricsController.QualifySegment), nameof(AdminBiometricsController.RevokeSegmentQualification) })
        {
            var gates = typeof(AdminBiometricsController).GetMethod(name)!.GetCustomAttributes<FeatureGateAttribute>(inherit: true).Select(gate => gate.Flag);
            Assert.Equal(both, gates.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>AC6: every message of the session and of the segments is in the Italian resources and in the English ones, translated.</summary>
    [Fact]
    public void IdentitySegmentEndpoints_MessagesAreLocalizedInItalianAndEnglish()
    {
        var italian = ResourceValues("SharedResources.resx");
        var english = ResourceValues("SharedResources.en.resx");
        string[] keys =
        [
            "IdentitySegmentInvalid", "IdentitySegmentReasonRequired", "IdentitySegmentNotQualified", "IdentitySegmentQualificationInvalid",
            "IdentitySegmentQualificationMismatch", "IdentitySegmentPolicyProvisional", "IdentitySegmentFingerprintUnavailable", "CheckInIdentityAlreadyClosed",
            "CheckInIdentityNoticeVersionUnknown",
        ];

        foreach (var key in keys)
        {
            Assert.True(italian.TryGetValue(key, out var it) && !string.IsNullOrWhiteSpace(it), $"{key} is not in SharedResources.resx.");
            Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"{key} is not in SharedResources.en.resx.");
            Assert.NotEqual(it, en);
        }

        Assert.Contains("{0}", italian["IdentitySegmentReasonRequired"], StringComparison.Ordinal);
        Assert.Contains("{0}", english["IdentitySegmentReasonRequired"], StringComparison.Ordinal);
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    private QualifyIdentitySegmentDto Qualification() => new()
    {
        PolicyVersion = _fingerprint.PolicyVersion,
        PolicyHash = _fingerprint.PolicyHash,
        WeightsHash = _fingerprint.WeightsHash,
        ReportSha256 = ReportHash,
        RequiredAssurance = IdentityAssuranceLevels.Consistency,
    };

    private BiometricSettingsController OrgController(Guid orgId, string userId = "auth0|admin")
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        return new BiometricSettingsController(Mock.Of<IBiometricSettingsService>(), resolver.Object) { ControllerContext = ContextOf(userId) };
    }

    private static AdminBiometricsController AdminController() =>
        new(Mock.Of<IBiometricModelStatusReader>(), Mock.Of<IBiometricSettingsService>()) { ControllerContext = ContextOf("auth0|staff") };

    private static void AssertNotFound(IActionResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal("not_found", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
    }

    private static ControllerContext ContextOf(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
            RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
        },
    };

    private static List<(MethodInfo Method, HttpMethodAttribute Verb)> RoutedActions(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => (Method: m, Verb: m.GetCustomAttributes<HttpMethodAttribute>(inherit: true).FirstOrDefault()))
            .Where(a => a.Verb is not null)
            .Select(a => (a.Method, a.Verb!))
            .ToList();

    private static Dictionary<string, string> ResourceValues(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        var path = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found."), "Casazen.Web", "Resources", fileName);
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(element => (string)element.Attribute("name")!, element => (string?)element.Element("value") ?? string.Empty, StringComparer.Ordinal);
    }

    /// <summary>A store of the three models, loaded, with fixed weight hashes.</summary>
    private sealed class LoadedModels : IBiometricModelStore
    {
        public BiometricModelsSnapshot Snapshot { get; } = new(
            BiometricModelsState.Ready,
            null,
            [
                new BiometricModelInfo(BiometricModelRoles.FaceDetector, "yunet", "2023mar", "MIT", "test", new string('1', 64), true, null),
                new BiometricModelInfo(BiometricModelRoles.AntiSpoofing, "minifasnet", "v2", "Apache-2.0", "test", new string('2', 64), true, null),
                new BiometricModelInfo(BiometricModelRoles.FaceRecognition, "sface", "2021dec", "Apache-2.0", "test", new string('3', 64), true, null),
            ]);

        public bool IsAvailable => true;

        public LoadedBiometricModel? FaceDetector => null;

        public LoadedBiometricModel? AntiSpoofing => null;

        public LoadedBiometricModel? FaceRecognition => null;
    }
}
