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

// E74/F02/US01/T02 (NF-198, NF-188, NF-194, NF-195, NF-196, decisions D2, D3, D4, D8, D9, D10): the builder of the acceptance scenario of the self-hosted identity verification on the backend. It
// starts the REAL host of the application (every controller, policy, filter, service and registration, with the four flags on in its own configuration), over the database of the factory
// (PostgreSQL in CI, the fallback of EF InMemory on a machine without it), and changes only what no test may do for real: the three face models and the readers of the document answer from a
// script (ScriptedModels, the doubles of the readers of the document step), the clock is the test's, the push is recorded and the code of the lock is a double. The decision is the policy's,
// taken by the real pipeline on what the doubles say. Everything is generated in code: no photograph of a person and no picture of a document is in the repository (it is public), and
// nothing here prints a frame, a name, a document number or an embedding.

/// <summary>
/// The store of the models of the host of the scenario: the three models loaded, each with a weight hash that a test can change (a new weight file of one model): the fingerprint a decision
/// is bound to (<see cref="IdentityConfigurationFingerprint"/>) reads the snapshot every time, so a change makes a qualified segment go back to observation without a restart.
/// </summary>
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

    /// <summary>A new weight file of the model of <paramref name="role"/>: its hash changes.</summary>
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

/// <summary>The push notifications the host queued, in order: the operators are told of a review by a push, and the scenario proves it.</summary>
internal sealed class RecordingPush : IPushNotificationService
{
    public ConcurrentQueue<(string DeliveryKey, PushAudience Audience, PushNotificationPayload Payload)> Sent { get; } = new();

    public bool Enqueue(string deliveryKey, PushAudience audience, PushNotificationPayload payload)
    {
        Sent.Enqueue((deliveryKey, audience, payload));
        return true;
    }
}

/// <summary>How a host of the scenario is configured: the defaults are the four flags on and the committed policy, calibrated for the test.</summary>
internal sealed record ScenarioOptions
{
    /// <summary><c>Features:CheckInIdentitySelfHosted</c>.</summary>
    public bool SelfHosted { get; init; } = true;

    /// <summary><c>Features:CheckInIdentityAutoApproval</c>.</summary>
    public bool AutoApproval { get; init; } = true;

    /// <summary><c>Features:CheckInIdentityReviewEvidence</c>.</summary>
    public bool ReviewEvidence { get; init; } = true;

    /// <summary><c>IdentityVerification:Presence:SequenceTimeoutMs</c>; the committed value when null.</summary>
    public int? SequenceTimeoutMs { get; init; }

    /// <summary>The version of the policy file of this host: another version is another file, so another hash, and the segments qualified for the first are void for it.</summary>
    public string PolicyVersion { get; init; } = "identity-policy-scenario-2026.11-1";
}

/// <summary>A stay of the scenario: a confirmed booking of an org on the agency plan, its guest (whose names and birth date are the ones the scripted MRZ says) and the link of its check-in.</summary>
internal sealed record ScenarioStay(string OwnerId, Guid OrgId, Guid PropertyId, Guid BookingId, Guid GuestId, Guid SessionId, string Token);

/// <summary>What one request of the presence got: the status, the body, the key and the proof it was sent with, and the frames.</summary>
internal sealed record PresenceAttempt(HttpStatusCode Status, JsonElement Body, string Key, string ChallengeToken, PreparedSequence Sent)
{
    /// <summary>The <c>result</c> of the answer: <c>ok</c>, <c>retry</c> or <c>operator</c>.</summary>
    public string? Result => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("result", out var result) ? result.GetString() : null;

    /// <summary>The guidance codes of the answer.</summary>
    public IReadOnlyList<string> Guidance => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("guidance", out var guidance)
        ? [.. guidance.EnumerateArray().Select(code => code.GetString()!)]
        : [];

    /// <summary>The lock code of the answer, null when it carries none.</summary>
    public string? LockCode => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("lockCode", out var code) ? code.GetString() : null;
}

/// <summary>
/// The host of the acceptance scenario with its doubles and its helpers: stays to seed, the calls of the guest (the opening, the consent, the two sides of the card, the challenge and the
/// sequence), the calls of the operators and of the platform administrators, a clock to move, and the database to read. One host for each test: the database is the factory's, and every
/// test seeds its own orgs.
/// </summary>
internal sealed class SelfHostedIdentityScenario : IAsyncDisposable
{
    /// <summary>The browser of the guests of the scenario: Chrome on Android (<see cref="IdentityChannels.AndroidChrome"/>).</summary>
    public const string AndroidChromeAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";

    /// <summary>The lock code the double of the code service releases.</summary>
    public const string LockCode = "4321";

    /// <summary>The format of the registry the scenario qualifies: the Italian electronic identity card.</summary>
    public const string FormatId = "it-cie-3.0";

    /// <summary>The identity of the platform staff who qualifies a segment.</summary>
    private const string StaffUserId = "auth0|identity-staff";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-identity-scenario-" + Guid.NewGuid().ToString("N"));
    private readonly List<HttpClient> _clients = [];

    private SelfHostedIdentityScenario(CasazenWebApplicationFactory factory, ScenarioOptions options)
    {
        Factory = factory;
        Options = options;
        Directory.CreateDirectory(_directory);

        // The policy of the plan, calibrated: a qualification is refused for a provisional one (NF-194). Its own file and its own pin, so that another host can run another policy.
        (PolicyPath, PolicyHash, Policy) = WriteCalibratedPolicy(_directory, options.PolicyVersion);

        AccessCodes.Setup(codes => codes.ReleaseToGuestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(LockCode);
        Host = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Configuration(options)));
            builder.ConfigureTestServices(services =>
            {
                // The clock of the test: it starts now and moves only when the test moves it.
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);

                // The models answer from a script, the store has weights that a test can change, and the readers of the document answer for the pictures of the scenario.
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

                // The push is recorded and the code of the lock is a double: neither is the business of this scenario.
                services.RemoveAll<IPushNotificationService>();
                services.AddSingleton<IPushNotificationService>(Push);
                services.RemoveAll<IAccessCodeService>();
                services.AddSingleton(AccessCodes.Object);
            });
        });
    }

    public CasazenWebApplicationFactory Factory { get; }

    public ScenarioOptions Options { get; }

    /// <summary>The host: the application as it runs, with the flags and the doubles of this scenario.</summary>
    public WebApplicationFactory<Program> Host { get; }

    /// <summary>The clock of the host.</summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>The doubles of the three models; the generated cards of the document step are answered by the stand-in of the portrait.</summary>
    public ScriptedModels Models { get; } = new() { Fallback = new PortraitStandInDetector() };

    public ScenarioModelStore Store { get; } = new();

    public RecordingPush Push { get; } = new();

    /// <summary>The service of the code of the lock: it releases <see cref="LockCode"/>, and the scenario verifies when it was asked.</summary>
    public Mock<IAccessCodeService> AccessCodes { get; } = new();

    public DocumentStepSelfHostedTests.ScriptedMrz Mrz { get; } = new();

    public DocumentStepSelfHostedTests.ScriptedZonedReader Zones { get; } = new();

    /// <summary>The policy in force in this host, as a value (the sequences are written from it).</summary>
    public IdentityDecisionPolicy Policy { get; }

    /// <summary>The path of the file of the policy of this host.</summary>
    public string PolicyPath { get; }

    /// <summary>The canonical hash of the policy of this host, the one its pin says.</summary>
    public string PolicyHash { get; }

    /// <summary>A sequence of a guest who does what the challenge asks, in order: what the policy accepts.</summary>
    public Func<IReadOnlyList<string>, ScriptedSequence> Genuine => steps => SequenceTestFrames.Genuine(Policy, steps);

    /// <summary>A host with <paramref name="options"/> on <paramref name="factory"/>.</summary>
    public static SelfHostedIdentityScenario Start(CasazenWebApplicationFactory factory, ScenarioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new SelfHostedIdentityScenario(factory, options ?? new ScenarioOptions());
    }

    /// <summary>
    /// The committed policy made calibrated (the committed file says it is provisional, and a provisional policy is never qualified) and given a version of its own, written to
    /// <paramref name="directory"/>: its path, its canonical hash (the pin the host is configured with) and the policy as a value. The committed file is never touched.
    /// </summary>
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

    // ───── clients ─────

    /// <summary>An anonymous client of a guest, with the browser of <paramref name="userAgent"/>.</summary>
    public HttpClient Guest(string userAgent = AndroidChromeAgent)
    {
        var client = Host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        _clients.Add(client);
        return client;
    }

    /// <summary>A signed-in client of a host (an owner, by default), as the test authentication gives it: the user and the roles of its token.</summary>
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

    /// <summary>A platform administrator.</summary>
    public HttpClient Admin() => Operator($"auth0|identity-admin-{Guid.NewGuid():N}", "Admin");

    // ───── data ─────

    /// <summary>
    /// A stay of an org on the agency plan (the org of <paramref name="ownerId"/>, or of a new owner): its booking is confirmed for one guest, whose names and birth date are the ones the
    /// scripted MRZ says, and a check-in link is open. Another stay of the same owner is another property and another booking of the same org.
    /// </summary>
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

    /// <summary>
    /// A stay whose guest opened the identity step, gave the biometric consent and sent the two sides of the card (the document is complete and the presence is the next step), with the
    /// browser of the scenario.
    /// </summary>
    public async Task<(ScenarioStay Stay, HttpClient Guest)> ReadyStayAsync(string? ownerId = null)
    {
        var stay = await SeedStayAsync(ownerId);
        var guest = Guest();
        await OpenAndConsentAsync(guest, stay);
        await CompleteDocumentAsync(guest, stay);
        return (stay, guest);
    }

    /// <summary>
    /// A person of the org of <paramref name="orgId"/> who owns none of its properties but one of their own: the token says the property owner and the person reaches only what it created
    /// (the account with no team of the org). Returns its id.
    /// </summary>
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

    /// <summary>Reads the database of the host in a scope of its own (the filters of the tenant are the caller's to lift).</summary>
    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        using var scope = Host.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>The log rows of a booking, oldest first.</summary>
    public Task<List<BiometricVerificationLog>> LogRowsAsync(Guid bookingId) =>
        ReadAsync(db => db.BiometricVerificationLogs.IgnoreQueryFilters().AsNoTracking().Where(log => log.BookingId == bookingId).OrderBy(log => log.CreatedAt).ToListAsync(Ct));

    /// <summary>The session as the database holds it.</summary>
    public Task<GuestCheckInSession> SessionAsync(Guid sessionId) =>
        ReadAsync(db => db.GuestCheckInSessions.IgnoreQueryFilters().AsNoTracking().SingleAsync(session => session.Id == sessionId, Ct));

    /// <summary>The evidence rows kept for a booking, deleted ones included.</summary>
    public Task<List<IdentityReviewEvidence>> EvidenceRowsAsync(Guid bookingId) =>
        ReadAsync(db => db.IdentityReviewEvidences.IgnoreQueryFilters().AsNoTracking().Where(evidence => evidence.BookingId == bookingId).OrderBy(evidence => evidence.CreatedAt).ToListAsync(Ct));

    /// <summary>The open "To do" of the identity verification of a booking.</summary>
    public Task<List<InternalActivity>> OpenTodosAsync(Guid bookingId) =>
        ReadAsync(db => db.InternalActivities.IgnoreQueryFilters().AsNoTracking()
            .Where(activity => activity.EntityType == InternalActivity.BookingEntityType
                               && activity.EntityId == bookingId
                               && activity.ActivityType == nameof(ActivityType.IdentityVerification)
                               && activity.CompletedAt == null)
            .ToListAsync(Ct));

    /// <summary>The pictures of the document the platform keeps for a booking, deleted ones included.</summary>
    public Task<List<DocumentImage>> DocumentImagesAsync(Guid bookingId) =>
        ReadAsync(db => db.DocumentImages.IgnoreQueryFilters().AsNoTracking().Where(image => image.BookingId == bookingId).ToListAsync(Ct));

    /// <summary>The events of a type on the bus for a booking.</summary>
    public Task<List<OutboxEvent>> EventsAsync(Guid bookingId, DomainEventType type) =>
        ReadAsync(db => db.OutboxEvents.IgnoreQueryFilters().AsNoTracking().Where(@event => @event.EntityId == bookingId && @event.EventType == type).ToListAsync(Ct));

    /// <summary>The lines of the activity log of an org of a type, oldest first.</summary>
    public Task<List<OrgActivityEntry>> ActivityAsync(Guid orgId, OrgActivityType type) =>
        ReadAsync(db => db.OrgActivityEntries.IgnoreQueryFilters().AsNoTracking().Where(entry => entry.OrgId == orgId && entry.Type == type).OrderBy(entry => entry.When).ToListAsync(Ct));

    /// <summary>Whether the object of a key is in the private bucket of the host.</summary>
    public async Task<bool> ObjectExistsAsync(string key)
    {
        using var scope = Host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFileStorage>().ExistsAsync(StorageBucket.Private, key, Ct);
    }

    /// <summary>The first bytes of an object of the private bucket as it is stored (what an evidence is in the bucket: ciphertext, never a picture).</summary>
    public async Task<byte[]> ObjectHeadAsync(string key, int count)
    {
        using var scope = Host.Services.CreateScope();
        await using var stream = await scope.ServiceProvider.GetRequiredService<IFileStorage>().OpenReadAsync(StorageBucket.Private, key, Ct)
            ?? throw new InvalidOperationException("The object is not in the bucket.");
        var head = new byte[count];
        var read = await stream.ReadAsync(head.AsMemory(), Ct);
        return head[..read];
    }

    // ───── the guest ─────

    /// <summary>The guest opens the identity step and gives the biometric consent, with the version of the text the page served: returns that version.</summary>
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

    /// <summary>The picture of the front of the card of the scenario, as a PNG (generated in code, the same for every stay).</summary>
    public static byte[] FrontPicture() => DocumentStepSelfHostedTests.Picture(DocumentSide.Front);

    /// <summary>The picture of the back of the card of the scenario.</summary>
    public static byte[] BackPicture() => DocumentStepSelfHostedTests.Picture(DocumentSide.Back);

    /// <summary>
    /// The guest sends the two sides of the card: the front first (the document is not complete without the back), then the back, whose MRZ the scripted reader knows. Once the document is
    /// complete the doubles of the models learn the portrait of the kept front, which the presence will be compared with.
    /// </summary>
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

    /// <summary>One picture of the document, as the page sends it: the field <c>document</c> and the field <c>side</c>.</summary>
    public async Task<JsonElement> UploadDocumentAsync(HttpClient guest, ScenarioStay stay, string side, byte[] picture)
    {
        using var form = new MultipartFormDataContent();
        var image = new ByteArrayContent(picture);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(image, "document", side + ".png");
        form.Add(new StringContent(side), "side");
        return await ReadOkAsync(await guest.PostAsync($"/api/public/checkin/{stay.Token}/document", form));
    }

    /// <summary>The guest asks for the challenge of the next attempt: its body (<c>challengeToken</c>, <c>steps</c>, ...).</summary>
    public async Task<JsonElement> ChallengeAsync(HttpClient guest, ScenarioStay stay) =>
        await ReadOkAsync(await guest.PostAsync($"/api/public/checkin/{stay.Token}/presence-challenge", null));

    /// <summary>
    /// One attempt as the page makes it: the challenge, the sequence <paramref name="script"/> writes for its moves (the models taught what it shows, with the liveness
    /// <paramref name="liveness"/> and the similarity <paramref name="similarity"/> when given), the clock moved to when the sequence reaches the server, and the request with the key
    /// <paramref name="key"/> (a new one by default).
    /// </summary>
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

    /// <summary>The first half of an attempt: the challenge, the sequence written for its moves and the clock moved to when the sequence reaches the server. Nothing is sent yet.</summary>
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

    /// <summary>The request of the presence: the frames, their timestamps (<paramref name="timestamps"/> when the test tampers with them), the proof and the key of the attempt.</summary>
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

    /// <summary>A new key of an attempt (a UUID: 36 letters, digits and hyphens).</summary>
    public static string NewKey() => Guid.NewGuid().ToString();

    // ───── the segments ─────

    /// <summary>
    /// The way the platform staff and the org make a segment automatic: the segment of the whole org for the Italian card on <paramref name="channel"/> is created, qualified for the policy
    /// and the weights in force (the report of the qualification is a hash that stands for it) and put in the automatic mode by the org. With the global flag on the segment may then approve
    /// (<paramref name="approvalAllowed"/> true); with it off it never may, whatever its mode says. Returns its id.
    /// </summary>
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

    // ───── the retention ─────

    /// <summary>One run of the hourly retention job of the identity verification, as the scheduler runs it, at the time of the clock of the host.</summary>
    public async Task RunRetentionAsync()
    {
        using var scope = Host.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<IdentityEvidencePurgeJob>(scope.ServiceProvider).ExecuteAsync(Ct);
    }

    /// <summary>What a platform administrator reads of the models and of the retention: <c>GET api/admin/biometrics/status</c>.</summary>
    public async Task<JsonElement> AdminStatusAsync() => await ReadOkAsync(await Admin().GetAsync("/api/admin/biometrics/status"));

    // ───── what an answer says ─────

    /// <summary>The body of a successful answer; a failure shows the status and the body of the problem (codes and texts of the application, never a picture).</summary>
    public static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri?.AbsolutePath}: {body}");
            return ParseBody(body);
        }
    }

    /// <summary>The <c>code</c> of a problem answer (<c>ApiProblem</c>), or null when the body is not one.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var body = ParseBody(await response.Content.ReadAsStringAsync(Ct));
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>The answer is the problem <paramref name="code"/> with the status <paramref name="status"/> (the guest or the host is told why, in a code the page translates).</summary>
    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(code, await ProblemCodeAsync(response));
        }
    }

    /// <summary>The rows of the CSV of the pilot as the cells of each one by the name of its column; the first line is the header, a byte order mark is not a cell.</summary>
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

    /// <summary>The CSV of the pilot of the caller, as rows.</summary>
    public static async Task<List<Dictionary<string, string>>> PilotRowsAsync(HttpClient host)
    {
        using var response = await host.GetAsync("/api/verification/metrics/export.csv", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ParseCsv(await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The <c>identity</c> counters of <c>GET api/verification/metrics</c> of the caller.</summary>
    public static async Task<JsonElement> IdentityMetricsAsync(HttpClient host) =>
        (await ReadOkAsync(await host.GetAsync("/api/verification/metrics"))).GetProperty("identity");

    /// <summary>The queue of the operator of the caller: the pending verifications.</summary>
    public static async Task<List<JsonElement>> PendingAsync(HttpClient host) =>
        [.. (await ReadOkAsync(await host.GetAsync("/api/verification/pending"))).EnumerateArray()];

    /// <summary>The live evidence of a booking as the reviewer lists it: <c>[{ id, kind, createdAt, expiresAt }]</c>.</summary>
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
            // The legal texts of the identity step (the consent the guest answers) are read from the folder the API ships: the host of the tests runs in the Testing environment, which serves the drafts.
            ["Legal:ContentPath"] = LegalTextFixtures.ShippedRoot(),
            ["IdentityVerification:PolicyFile"] = PolicyPath,
            ["IdentityVerification:PolicySha256"] = PolicyHash,

            // The guests of the scenario send more in a minute than the limits of production allow: they are out of the way, as the factory does for the rest.
            ["RateLimiting:GuestIdentity:PermitLimit"] = "1000",
            ["RateLimiting:GuestIdentityUpload:PermitLimit"] = "1000",

            // The real store of the models is asked for nothing here: with no hash to prove it stops at once and loads (and downloads) nothing, and the store of the scenario answers instead.
            ["Biometrics:FaceDetector:Sha256"] = string.Empty,
            ["Biometrics:AntiSpoofing:Sha256"] = string.Empty,
            ["Biometrics:FaceRecognition:Sha256"] = string.Empty,
        };
        if (options.SequenceTimeoutMs is { } timeout)
            configuration["IdentityVerification:Presence:SequenceTimeoutMs"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return configuration;
    }
}
