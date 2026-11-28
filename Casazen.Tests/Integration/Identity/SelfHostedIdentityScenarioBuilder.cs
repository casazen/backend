using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Identity;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration.Identity;

internal sealed class ScenarioModelStore : IBiometricModelStore
{
    private static readonly string[] Roles = [BiometricModelRoles.FaceDetector, BiometricModelRoles.AntiSpoofing, BiometricModelRoles.FaceRecognition];

    private readonly object _gate = new();
    private readonly Dictionary<string, int> _generation = Roles.ToDictionary(role => role, _ => 0, StringComparer.Ordinal);
    private BiometricModelsSnapshot _snapshot;

    public ScenarioModelStore()
    {
        _snapshot = Build();
    }

    public BiometricModelsSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public bool IsAvailable => true;

    public LoadedBiometricModel? FaceDetector => null;

    public LoadedBiometricModel? AntiSpoofing => null;

    public LoadedBiometricModel? FaceRecognition => null;

    public void ChangeWeights(string role)
    {
        lock (_gate)
        {
            _generation[role]++;
            _snapshot = Build();
        }
    }

    private BiometricModelsSnapshot Build() =>
        new(
            BiometricModelsState.Ready,
            null,
            [.. Roles.Select(role => new BiometricModelInfo(role, "scenario-" + role, "1", "test", "generated in code", WeightHash(role, _generation[role]), true, null))]);

    private static string WeightHash(string role, int generation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{role}:{generation}")));
}

internal sealed class RecordingPush : IPushNotificationService
{
    public ConcurrentQueue<(string DeliveryKey, PushAudience Audience, PushNotificationPayload Payload)> Sent { get; } = new();

    public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
    {
        Sent.Enqueue((deliveryKey, audience, payload));
        return true;
    }
}

internal sealed record ScenarioOptions
{
    public bool SelfHosted { get; init; } = true;

    public bool AutoApproval { get; init; } = true;

    public bool ReviewEvidence { get; init; } = true;

    public int? SequenceTimeoutMs { get; init; }

    public string PolicyVersion { get; init; } = "identity-policy-scenario-2026.11-1";
}

internal sealed record ScenarioStay(string OwnerId, Guid OrgId, Guid PropertyId, Guid BookingId, Guid GuestId, Guid SessionId, string Token);

internal sealed record PresenceAttempt(HttpStatusCode Status, JsonElement Body, string Key, string ChallengeToken, PreparedSequence Sent)
{
    public string? Result => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("result", out var result) ? result.GetString() : null;

    public IReadOnlyList<string> Guidance => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("guidance", out var guidance)
        ? [.. guidance.EnumerateArray().Select(code => code.GetString()!)]
        : [];

    public string? LockCode => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("lockCode", out var code) ? code.GetString() : null;
}

internal sealed class SelfHostedIdentityScenario : IAsyncDisposable
{
    public const string AndroidChromeAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";

    public const string LockCode = "4321";

    public const string FormatId = "it-cie-3.0";

    private const string StaffUserId = "auth0|identity-staff";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-identity-scenario-" + Guid.NewGuid().ToString("N"));
    private readonly List<HttpClient> _clients = [];

    private SelfHostedIdentityScenario(CasazenWebApplicationFactory factory, ScenarioOptions options)
    {
        Factory = factory;
        Options = options;
        Directory.CreateDirectory(_directory);

        // A provisional policy is never qualified: the host runs a calibrated copy with its own version and pin, so that another host can run another policy.
        (PolicyPath, PolicyHash, Policy) = WriteCalibratedPolicy(_directory, options.PolicyVersion);

        AccessCodes.Setup(codes => codes.ReleaseToGuestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(LockCode);
        Host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Configuration(options)));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);

                services.RemoveAll<IFaceDetector>();
                services.AddSingleton<IFaceDetector>(Models);
                services.RemoveAll<IFaceEmbedder>();
                services.AddSingleton<IFaceEmbedder>(Models);
                services.RemoveAll<IAntiSpoofingService>();
                services.AddSingleton<IAntiSpoofingService>(Models);
                services.RemoveAll<IBiometricModelStore>();
                services.AddSingleton<IBiometricModelStore>(Store);
                services.RemoveAll<IIdentityVerificationAgent>();
                services.AddSingleton<IIdentityVerificationAgent>(new AcceptingAgent());
                services.RemoveAll<IMrzExtractionService>();
                services.AddSingleton<IMrzExtractionService>(Mrz);
                services.RemoveAll<IDocumentOcrService>();
                services.AddSingleton<IDocumentOcrService>(new DocumentStepSelfHostedTests.EvidentDocumentOcr());
                services.RemoveAll<IZonedFieldReader>();
                services.AddSingleton<IZonedFieldReader>(Zones);

                services.RemoveAll<IPushNotificationService>();
                services.AddSingleton<IPushNotificationService>(Push);
                services.RemoveAll<IAccessCodeService>();
                services.AddSingleton(AccessCodes.Object);
            });
        });
    }

    public CasazenWebApplicationFactory Factory { get; }

    public ScenarioOptions Options { get; }

    public WebApplicationFactory<Program> Host { get; }

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public ScriptedModels Models { get; } = new() { Fallback = new PortraitStandInDetector() };

    public ScenarioModelStore Store { get; } = new();

    public RecordingPush Push { get; } = new();

    public Mock<IAccessCodeService> AccessCodes { get; } = new();

    public DocumentStepSelfHostedTests.ScriptedMrz Mrz { get; } = new();

    public DocumentStepSelfHostedTests.ScriptedZonedReader Zones { get; } = new();

    public IdentityDecisionPolicy Policy { get; }

    public string PolicyPath { get; }

    public string PolicyHash { get; }

    public Func<IReadOnlyList<string>, ScriptedSequence> Genuine => steps => SequenceTestFrames.Genuine(Policy, steps);

    public static SelfHostedIdentityScenario Start(CasazenWebApplicationFactory factory, ScenarioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new SelfHostedIdentityScenario(factory, options ?? new ScenarioOptions());
    }

    internal static (string Path, string Hash, IdentityDecisionPolicy Policy) WriteCalibratedPolicy(string directory, string version)
    {
        var text = IdentityPolicyTestFiles.Apply(
            IdentityPolicyTestFiles.ShippedText(),
            ("provisional", "false"),
            ("version", $"\"{version}\""));
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
        var path = Path.Combine(directory, "identity-policy-scenario.json");
        File.WriteAllBytes(path, bytes);
        var hash = IdentityPolicyProvider.CanonicalSha256(bytes) ?? throw new InvalidOperationException("The policy of the scenario is not strict JSON.");
        var policy = JsonSerializer.Deserialize<IdentityDecisionPolicy>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The policy of the scenario is not a policy.");
        return (path, hash, policy);
    }

    public HttpClient Guest(string userAgent = AndroidChromeAgent)
    {
        var client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        _clients.Add(client);
        return client;
    }

    public HttpClient Operator(string userId, string roles = "PropertyOwner")
    {
        var client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.SchemeName, "test");
        client.DefaultRequestHeaders.Add("X-Test-User", userId);
        if (!string.IsNullOrEmpty(roles))
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);

        _clients.Add(client);
        return client;
    }

    public HttpClient Admin() => Operator($"auth0|identity-admin-{Guid.NewGuid():N}", "Admin");

    // The Agency plan is the one with the identity verification on by default; the names and the birth date of the guest are those of the scripted MRZ,
    // or the document would conflict with the booking.
    public async Task<ScenarioStay> SeedStayAsync(string? ownerId = null)
    {
        ownerId ??= $"auth0|identity-owner-{Guid.NewGuid():N}";
        var seed = await Factory.SeedConfirmedBookingWithTokenAsync(ownerId: ownerId);
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().SingleAsync(b => b.Id == seed.BookingId, Ct);
        var org = await db.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == booking.OrgId, Ct);
        var guest = await db.Guests.IgnoreQueryFilters().SingleAsync(g => g.Id == seed.GuestId, Ct);
        org.PlanTier = PlanTier.Agency;
        booking.NumberOfGuests = 1;
        guest.FirstName = DocumentStepSelfHostedTests.GivenNames;
        guest.LastName = DocumentStepSelfHostedTests.Surname;
        guest.DateOfBirth = new DateTime(1987, 3, 19, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync(Ct);

        var token = await new GuestCheckInService(db, NullLogger<GuestCheckInService>.Instance).CreateSessionAsync(booking.Id, booking.OrgId);
        var sessionId = await db.GuestCheckInSessions.IgnoreQueryFilters().Where(s => s.BookingId == booking.Id).Select(s => s.Id).SingleAsync(Ct);
        return new ScenarioStay(ownerId, booking.OrgId, booking.PropertyId, booking.Id, guest.Id, sessionId, token);
    }

    public async Task<(ScenarioStay Stay, HttpClient Guest)> ReadyStayAsync(string? ownerId = null)
    {
        var stay = await SeedStayAsync(ownerId);
        var guest = Guest();
        await OpenAndConsentAsync(guest, stay);
        await CompleteDocumentAsync(guest, stay);
        return (stay, guest);
    }

    // The account with no team of the org: the token says property owner, so it reaches only the properties it created.
    public async Task<string> SeedColleagueAsync(Guid orgId)
    {
        var userId = $"auth0|identity-colleague-{Guid.NewGuid():N}";
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { Id = userId, Email = $"{Guid.NewGuid():N}@example.com", FirstName = "Collega", LastName = "Host", OrgId = orgId, IsActive = true };
        db.Users.Add(user);
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        db.Properties.Add(HostScopeScenario.NewProperty(orgId, userId, "Casa del collega"));
        await db.SaveChangesAsync(Ct);
        return userId;
    }

    public async Task<string> SeedSelectedCollaboratorAsync(ScenarioStay stay, bool onTheStay)
    {
        var userId = await OrgTeamHttp.AddMemberAsync(Factory, stay.OrgId, OrgRole.Collaborator, ["short-rent"]);
        using var scope = Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var granted = stay.PropertyId;
        if (!onTheStay)
        {
            var other = HostScopeScenario.NewProperty(stay.OrgId, stay.OwnerId, "Casa assegnata");
            db.Properties.Add(other);
            granted = other.Id;
        }

        var member = await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.UserId == userId, Ct);
        member.PropertyScope = PropertyScope.Selected;
        db.PropertyMemberAccesses.Add(new PropertyMemberAccess { OrgId = stay.OrgId, UserId = userId, PropertyId = granted });
        await db.SaveChangesAsync(Ct);
        return userId;
    }

    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var scope = Host.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task<List<BiometricVerificationLog>> LogRowsAsync(Guid bookingId) =>
        ReadAsync(db => db.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().Where(log => log.BookingId == bookingId).OrderBy(log => log.CreatedAt).ToListAsync(Ct));

    public Task<GuestCheckInSession> SessionAsync(Guid sessionId) =>
        ReadAsync(db => db.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == sessionId, Ct));

    public Task<List<IdentityReviewEvidence>> EvidenceRowsAsync(Guid bookingId) =>
        ReadAsync(db => db.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().Where(evidence => evidence.BookingId == bookingId).OrderBy(evidence => evidence.CreatedAt).ToListAsync(Ct));

    public Task<List<InternalActivity>> OpenTodosAsync(Guid bookingId) =>
        ReadAsync(db => db.InternalActivities.IgnoreQueryFilters().AsNoTracking()
            .Where(activity => activity.EntityType == InternalActivity.BookingEntityType
                               && activity.EntityId == bookingId
                               && activity.ActivityType == nameof(ActivityType.IdentityVerification)
                               && activity.CompletedAt == null)
            .ToListAsync(Ct));

    public Task<List<DocumentImage>> DocumentImagesAsync(Guid bookingId) =>
        ReadAsync(db => db.DocumentImages.IgnoreQueryFilters().AsNoTracking().Where(image => image.BookingId == bookingId).ToListAsync(Ct));

    public Task<List<OutboxEvent>> EventsAsync(Guid bookingId, DomainEventType type) =>
        ReadAsync(db => db.OutboxEvents.IgnoreQueryFilters().AsNoTracking().Where(@event => @event.EntityId == bookingId && @event.EventType == type).ToListAsync(Ct));

    public Task<List<OrgActivityEntry>> ActivityAsync(Guid orgId, OrgActivityType type) =>
        ReadAsync(db => db.OrgActivityEntries.IgnoreQueryFilters().AsNoTracking().Where(entry => entry.OrgId == orgId && entry.Type == type).OrderBy(entry => entry.When).ToListAsync(Ct));

    public async Task<bool> ObjectExistsAsync(string key)
    {
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFileStorage>().ExistsAsync(StorageBucket.Private, key, Ct);
    }

    public async Task<byte[]> ObjectHeadAsync(string key, int count)
    {
        using var scope = Host.Services.CreateScope();
        await using var stream = await scope.ServiceProvider.GetRequiredService<IFileStorage>().OpenReadAsync(StorageBucket.Private, key, Ct)
            ?? throw new InvalidOperationException("The object is not in the bucket.");
        var head = new byte[count];
        var read = await stream.ReadAsync(head.AsMemory(), Ct);
        return head[..read];
    }

    public async Task<string> OpenAndConsentAsync(HttpClient guest, ScenarioStay stay)
    {
        var texts = await ReadOkAsync(await guest.GetAsync($"/api/public/checkin/{stay.Token}/legal-texts"));
        var version = texts.GetProperty("texts").EnumerateArray()
            .First(text => text.GetProperty("kind").GetString() == CheckInLegalTextKinds.BiometricConsent)
            .GetProperty("version").GetString()!;
        var opened = await ReadOkAsync(await guest.PostAsJsonAsync($"/api/public/checkin/{stay.Token}/identity/open", new { noticeVersion = version }));
        Assert.True(opened.GetProperty("state").GetProperty("opened").GetBoolean());
        var consent = await ReadOkAsync(await guest.PostAsJsonAsync($"/api/public/checkin/{stay.Token}/biometric-consent", new { consentGiven = true, consentVersion = version }));
        Assert.True(consent.GetProperty("consentRecorded").GetBoolean());
        return version;
    }

    public static byte[] FrontPicture() => DocumentStepSelfHostedTests.Picture(DocumentSide.Front);

    public static byte[] BackPicture() => DocumentStepSelfHostedTests.Picture(DocumentSide.Back);

    // The presence is compared with the portrait of the kept front, which the doubles of the models learn once the document is complete.
    public async Task CompleteDocumentAsync(HttpClient guest, ScenarioStay stay)
    {
        var front = FrontPicture();
        var back = BackPicture();
        Mrz.Add(back, DocumentStepSelfHostedTests.CieMrz());
        Zones.Fields[DocumentSide.Front] = DocumentStepSelfHostedTests.FrontFields();

        var first = await UploadDocumentAsync(guest, stay, "front", front);
        Assert.False(first.GetProperty("acceptable").GetBoolean());
        var second = await UploadDocumentAsync(guest, stay, "back", back);
        Assert.True(second.GetProperty("acceptable").GetBoolean());
        Models.UseDocument(front);
    }

    public async Task<JsonElement> UploadDocumentAsync(HttpClient guest, ScenarioStay stay, string side, byte[] picture)
    {
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(picture);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "document", side + ".png");
        form.Add(new StringContent(side), "side");
        return await ReadOkAsync(await guest.PostAsync($"/api/public/checkin/{stay.Token}/document", form));
    }

    public async Task<JsonElement> ChallengeAsync(HttpClient guest, ScenarioStay stay) =>
        await ReadOkAsync(await guest.PostAsync($"/api/public/checkin/{stay.Token}/presence-challenge", null));

    public async Task<PresenceAttempt> AttemptAsync(
        HttpClient guest,
        ScenarioStay stay,
        Func<IReadOnlyList<string>, ScriptedSequence> script,
        double? liveness = null,
        double? similarity = null,
        string? key = null)
    {
        var (challengeToken, sent) = await PrepareAttemptAsync(guest, stay, script, liveness, similarity);
        return await SendPresenceAsync(guest, stay, challengeToken, sent, key ?? NewKey());
    }

    // The clock moves to when the sequence reaches the server: a recording longer than the time since the challenge is refused.
    public async Task<(string ChallengeToken, PreparedSequence Sent)> PrepareAttemptAsync(
        HttpClient guest,
        ScenarioStay stay,
        Func<IReadOnlyList<string>, ScriptedSequence> script,
        double? liveness = null,
        double? similarity = null)
    {
        var challenge = await ChallengeAsync(guest, stay);
        var steps = challenge.GetProperty("steps").EnumerateArray().Select(step => step.GetString()!).ToList();
        if (similarity is { } declared)
            Models.Similarity = declared;

        var sent = Models.Prepare(script(steps), Policy, liveness);
        Clock.Advance(sent.ReceivedAfterIssue);
        return (challenge.GetProperty("challengeToken").GetString()!, sent);
    }

    public async Task<PresenceAttempt> SendPresenceAsync(HttpClient guest, ScenarioStay stay, string challengeToken, PreparedSequence sent, string key, string? timestamps = null)
    {
        using var form = new MultipartFormDataContent();
        foreach (var jpeg in sent.Jpegs)
        {
            var part = new ByteArrayContent(jpeg);
            part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(part, "frames", "frame.jpg");
        }

        form.Add(new StringContent(timestamps ?? sent.TimestampsJson), "timestamps");
        form.Add(new StringContent(challengeToken), "challengeToken");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/public/checkin/{stay.Token}/presence") { Content = form };
        request.Headers.Add(IdentityHeaders.IdempotencyKey, key);
        using var response = await guest.SendAsync(request, Ct);
        return new PresenceAttempt(response.StatusCode, ParseBody(await response.Content.ReadAsStringAsync(Ct)), key, challengeToken, sent);
    }

    public static string NewKey() => Guid.NewGuid().ToString();

    // A hash stands for the report of the qualification.
    public async Task<Guid> QualifySegmentAsync(ScenarioStay stay, string channel = IdentityChannels.AndroidChrome, bool approvalAllowed = true)
    {
        using var scope = Host.Services.CreateScope();
        var segments = scope.ServiceProvider.GetRequiredService<IIdentitySegmentService>();
        var fingerprint = scope.ServiceProvider.GetRequiredService<IdentityConfigurationFingerprint>().Current()
            ?? throw new InvalidOperationException("There is no fingerprint: the policy or the models of the host are not usable.");
        var segment = await segments.CreateAsync(stay.OrgId, null, FormatId, channel, StaffUserId, Ct);
        var qualified = await segments.QualifyAsync(
            stay.OrgId, segment.Id, fingerprint.PolicyVersion, fingerprint.PolicyHash, fingerprint.WeightsHash, new string('a', 64), IdentityAssuranceLevels.Consistency, StaffUserId, Ct);
        Assert.True(qualified is { Qualified: true }, "The segment must be qualified for the policy and the weights in force.");
        var automatic = await segments.SetModeAsync(stay.OrgId, segment.Id, IdentitySegmentMode.Automatic, null, stay.OwnerId, Ct);
        Assert.Equal(approvalAllowed, automatic is { AutoApprovalAllowed: true });
        return segment.Id;
    }

    public async Task RunRetentionAsync()
    {
        using var scope = Host.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<IdentityEvidencePurgeJob>(scope.ServiceProvider).ExecuteAsync(Ct);
    }

    public async Task<JsonElement> AdminStatusAsync() => await ReadOkAsync(await Admin().GetAsync("/api/admin/biometrics/status"));

    // A failure shows the status and the body of the problem: codes and texts of the application, never a picture.
    public static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri?.AbsolutePath}: {body}");
            return ParseBody(body);
        }
    }

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var body = ParseBody(await response.Content.ReadAsStringAsync(Ct));
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, await ProblemCodeAsync(response));
        }
    }

    // The file starts with a byte order mark, which is not a cell.
    public static List<Dictionary<string, string>> ParseCsv(string csv)
    {
        var lines = csv.TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(IdentityPilotExport.Header, lines[0]);
        return
        [
            .. lines.Skip(1).Select(line =>
            {
                var cells = line.Split(',');
                Assert.Equal(IdentityPilotExport.Columns.Count, cells.Length);
                return IdentityPilotExport.Columns.Select((column, index) => (column, cell: cells[index])).ToDictionary(pair => pair.column, pair => pair.cell, StringComparer.Ordinal);
            }),
        ];
    }

    public static async Task<List<Dictionary<string, string>>> PilotRowsAsync(HttpClient host)
    {
        using var response = await host.GetAsync("/api/verification/metrics/export.csv", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ParseCsv(await response.Content.ReadAsStringAsync(Ct));
    }

    public static async Task<JsonElement> IdentityMetricsAsync(HttpClient host) =>
        (await ReadOkAsync(await host.GetAsync("/api/verification/metrics"))).GetProperty("identity");

    public static async Task<List<JsonElement>> PendingAsync(HttpClient host) =>
        [.. (await ReadOkAsync(await host.GetAsync("/api/verification/pending"))).EnumerateArray()];

    public static async Task<List<JsonElement>> EvidenceListAsync(HttpClient host, Guid bookingId) =>
        [.. (await ReadOkAsync(await host.GetAsync($"/api/verification/{bookingId}/evidence"))).EnumerateArray()];

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            client.Dispose();

        await Host.DisposeAsync();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static JsonElement ParseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return JsonSerializer.SerializeToElement(new { });

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { notJson = true });
        }
    }

    private Dictionary<string, string?> Configuration(ScenarioOptions options)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Features:CheckInIdentityVerification"] = "true",
            ["Features:CheckInIdentitySelfHosted"] = options.SelfHosted ? "true" : "false",
            ["Features:CheckInIdentityAutoApproval"] = options.AutoApproval ? "true" : "false",
            ["Features:CheckInIdentityReviewEvidence"] = options.ReviewEvidence ? "true" : "false",
            // The Testing environment serves the drafts of the legal texts, read from the folder the API ships.
            ["Legal:ContentPath"] = LegalTextFixtures.ShippedRoot(),
            ["IdentityVerification:PolicyFile"] = PolicyPath,
            ["IdentityVerification:PolicySha256"] = PolicyHash,

            // The scenario sends more requests a minute than the limits of production allow.
            ["RateLimiting:GuestIdentity:PermitLimit"] = "1000",
            ["RateLimiting:GuestIdentityUpload:PermitLimit"] = "1000",

            // An empty hash makes the real store of the models stop at once, without loading or downloading anything: the store of the scenario answers instead.
            ["Biometrics:FaceDetector:Sha256"] = string.Empty,
            ["Biometrics:AntiSpoofing:Sha256"] = string.Empty,
            ["Biometrics:FaceRecognition:Sha256"] = string.Empty,
        };
        if (options.SequenceTimeoutMs is { } timeout)
            configuration["IdentityVerification:Presence:SequenceTimeoutMs"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return configuration;
    }
}
