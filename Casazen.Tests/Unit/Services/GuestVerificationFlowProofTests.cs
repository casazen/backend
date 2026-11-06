using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02/T04 proofs of the guest verification flow: no lock code leaves without a passed verification (AC-NF50-9), and nothing of the
/// selfie reaches the database, the storage, a queue or the logs (AC-NF50-10).
/// </summary>
public class GuestVerificationFlowProofTests
{
    private const string LockCode = "7391";
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Trust and data rule (AC-NF50-3, AC-NF50-9): a retry, a failed attempt, a review, a refusal and an error neither return nor send the code,
    /// and the review, the refusal and the error alert the operator; only a passed verification returns it and e-mails it, once per channel.
    /// </summary>
    [Fact]
    public async Task GuestVerificationFlow_NoCodeWithoutPassedVerification_Test()
    {
        await using var world = await World.CreateAsync();

        // A capture to take again, a failed attempt, then a review: no code, and the review goes to the operator.
        var reviewed = await world.ReadyStayAsync("Anna", "Bianchi");
        world.Agent.Next(Outcome(IdentityVerificationResult.Recapture));
        world.Agent.Next(Outcome(IdentityVerificationResult.NotOk));
        world.Agent.Next(Outcome(IdentityVerificationResult.Review));
        Assert.Equal(IdentityVerificationGuestResults.Retry, (await world.SelfieAsync(reviewed)).Result);
        var failed = await world.SelfieAsync(reviewed);
        Assert.Equal(IdentityVerificationGuestResults.Retry, failed.Result);
        Assert.Null(failed.LockCode);
        var review = await world.SelfieAsync(reviewed);
        Assert.Equal(IdentityVerificationGuestResults.Operator, review.Result);
        Assert.Null(review.LockCode);

        // A refusal of the consent: the operator, no code.
        var refused = await world.StayAsync("Bruno", "Conti");
        await world.Flow().RecordConsentAsync(refused, consentGiven: false, null, CancellationToken.None);

        // An error of the verification: the operator, no code.
        var broken = await world.ReadyStayAsync("Carla", "Dini");
        world.Agent.Fail(new InvalidOperationException("model crashed"));
        var error = await world.SelfieAsync(broken);
        Assert.Equal(IdentityVerificationGuestResults.Operator, error.Result);
        Assert.Null(error.LockCode);

        Assert.Empty(world.Emails);
        Assert.False(await world.Db.AccessCodeReleaseLogs.AnyAsync());
        Assert.Equal(
            [VerificationAlertReasons.Review, VerificationAlertReasons.ConsentRefused, VerificationAlertReasons.TechnicalError],
            world.Alerts.Select(a => a.Reason));

        // A passed verification: the code in the answer and e-mailed at once, through the gate, one release per channel.
        var passed = await world.ReadyStayAsync("Dario", "Esposito");
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        var ok = await world.SelfieAsync(passed);
        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        Assert.Equal(LockCode, ok.LockCode);
        var email = Assert.Single(world.Emails);
        Assert.Equal(EmailTemplates.Names.GuestAccessCode, email.Template);
        Assert.Contains(LockCode, email.Content.HtmlBody, StringComparison.Ordinal);
        var releases = await world.Db.AccessCodeReleaseLogs.ToListAsync();
        Assert.All(releases, r => Assert.Equal(passed.BookingId, r.BookingId));
        Assert.Equal(
            [AccessCodeReleaseChannels.CheckInScreen, AccessCodeReleaseChannels.Email],
            releases.Select(r => r.Channel).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Privacy rule (AC-NF50-6, AC-NF50-10): during the selfie step nothing is written to the storage, the log row holds codes and numbers, no
    /// queue or log carries the selfie or the name of the guest, and the kept photo of the document is deleted once the verification passed.
    /// </summary>
    [Fact]
    public async Task GuestVerificationFlow_NothingPersistedFromTheSelfie_Test()
    {
        // The flow has no way to a job queue: nothing it handles can become the argument of a background job.
        Assert.DoesNotContain(
            typeof(IdentityVerificationFlowService).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(IBackgroundJobClient));

        await using var world = await World.CreateAsync();
        var session = await world.ReadyStayAsync("Elena", "Ferrari");
        Assert.Single(world.Storage.Objects);
        var puts = world.Storage.Puts;
        var documentCheck = session.DocumentCheckJson;

        var selfie = TestImages.Png(96, 96, seed: 42);
        var marker = Convert.ToBase64String(selfie);
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        var ok = await world.SelfieAsync(session, selfie);

        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        Assert.Equal(selfie, world.Agent.LastSelfie);

        // No storage write, and the kept photo of the document is gone.
        Assert.Equal(puts, world.Storage.Puts);
        Assert.Empty(world.Storage.Objects);
        Assert.All(await world.Db.DocumentImages.ToListAsync(), image => Assert.NotNull(image.DeletedAt));
        Assert.Equal(documentCheck, session.DocumentCheckJson);

        // The log row: codes, numbers, model and thresholds.
        var row = await world.Db.BiometricVerificationLogs.SingleAsync();
        Assert.Equal(VerificationMethod.Biometric, row.Method);
        Assert.Equal(BiometricOutcome.Accept, row.Outcome);
        foreach (var text in new[] { row.ReasonsJson, row.ThresholdsJson, row.ModelName, row.ModelVersion, row.OperatorNote ?? string.Empty })
        {
            Assert.DoesNotContain(marker[..32], text, StringComparison.Ordinal);
            Assert.DoesNotContain("Elena", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ferrari", text, StringComparison.OrdinalIgnoreCase);
        }

        // The queue holds the e-mail of the code only, and no log carries the selfie or the guest.
        Assert.All(world.Emails, e => Assert.DoesNotContain(marker[..32], e.Content.HtmlBody, StringComparison.Ordinal));
        Assert.All(world.Logs, line =>
        {
            Assert.DoesNotContain(marker[..32], line, StringComparison.Ordinal);
            Assert.DoesNotContain("Elena", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Ferrari", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IdentityVerificationOutcome Outcome(IdentityVerificationResult result, bool lastAttempt = false) => new(
        result,
        result == IdentityVerificationResult.Ok
            ? [new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Passed, 0.93, 0.85)]
            : [new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Failed, 0.31, 0.70)],
        result == IdentityVerificationResult.Ok ? [] : [CaptureGuidanceCodes.RetakeSelfie],
        lastAttempt,
        "AuraFace-v1",
        "1.0.0",
        new VerificationThresholds(0.85, 0.70, 0.7, 3, CaptureQualityRules.Version, Provisional: true),
        840);

    /// <summary>One org on the Agency plan (the biometric verification on by default), a property with a lock code, the flow and its fakes.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"guest-verification-{Guid.NewGuid()}").Options;

        private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
        private readonly Guid _orgId = Guid.NewGuid();
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly Mock<IEmailQueue> _queue = new();

        private World()
        {
            Db = new AppDbContext(_options, null, _dataProtection);
            _queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
                .Callback<string?, EmailContent, string>((to, content, template) => Emails.Add((to, content, template)))
                .Returns(true);
        }

        public AppDbContext Db { get; }

        public ScriptedAgent Agent { get; } = new();

        public RecordingAlerts Alerts { get; } = new();

        public MemoryStorage Storage { get; } = new();

        public List<(string? To, EmailContent Content, string Template)> Emails { get; } = [];

        public List<string> Logs { get; } = [];

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            world.Db.Orgs.Add(new OrgEntity
            {
                Id = world._orgId,
                Name = "Agenzia verifiche",
                Slug = $"verifiche-{world._orgId:N}"[..20],
                DisplayName = "Agenzia verifiche",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Agency,
            });
            world.Db.Properties.Add(new Property
            {
                Id = world._propertyId,
                OrgId = world._orgId,
                OwnerId = "auth0|owner",
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Country = "IT",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            });
            world.Db.Set<PropertyAccessCode>().Add(new PropertyAccessCode
            {
                OrgId = world._orgId,
                PropertyId = world._propertyId,
                EncryptedCode = LockCode,
                UpdatedAt = Now.UtcDateTime,
            });
            await world.Db.SaveChangesAsync();
            return world;
        }

        public IdentityVerificationFlowService Flow() => new(
            Db,
            Agent,
            Alerts,
            new AccessCodeService(Db, new ListLogger<AccessCodeService>(Logs)),
            _queue.Object,
            new ReadableMrz(),
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(Db, Mock.Of<IAlloggiatiCodeTableService>()),
            Storage,
            _dataProtection,
            new FixedTimeProvider(Now),
            new ListLogger<IdentityVerificationFlowService>(Logs));

        /// <summary>A confirmed stay of an adult booker with an open check-in session (loaded as the public endpoint loads it).</summary>
        public async Task<GuestCheckInSession> StayAsync(string firstName, string lastName)
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                FirstName = firstName,
                LastName = lastName,
                Email = $"{firstName.ToLowerInvariant()}@example.com",
                DateOfBirth = new DateTime(1990, 1, 1),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = _orgId,
                PropertyId = _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 12),
                CheckOutDate = new DateTime(2026, 11, 14),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            var session = new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = _orgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.UtcDateTime.AddDays(10),
                Status = GuestCheckInSessionStatus.InCompilazione,
                Language = "it",
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            await Db.SaveChangesAsync();

            return await Db.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .SingleAsync(s => s.Id == session.Id);
        }

        /// <summary>A stay whose guest gave the consent and uploaded the document (kept encrypted: the property is not in Ticino).</summary>
        public async Task<GuestCheckInSession> ReadyStayAsync(string firstName, string lastName)
        {
            var session = await StayAsync(firstName, lastName);
            var flow = Flow();
            Assert.Equal(
                BiometricConsentStatus.Recorded,
                (await flow.RecordConsentAsync(session, true, "TEST-biometric-v1", CancellationToken.None)).Status);
            using var document = new MemoryStream(TestImages.Png(120, 80, seed: 3));
            var read = await flow.SubmitDocumentAsync(session, document, CancellationToken.None);
            Assert.True(read.DocumentStored);
            return session;
        }

        public async Task<SelfieStepResult> SelfieAsync(GuestCheckInSession session, byte[]? selfie = null)
        {
            using var stream = new MemoryStream(selfie ?? TestImages.Png(96, 96, seed: 5));
            var result = await Flow().SubmitSelfieAsync(session, stream, null, CancellationToken.None);
            Assert.Equal(SelfieStepStatus.Done, result.Status);
            return result;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    /// <summary>An agent that answers a script, accepts every capture and keeps the last selfie it read (to prove what reached it).</summary>
    private sealed class ScriptedAgent : IIdentityVerificationAgent
    {
        private readonly Queue<Func<IdentityVerificationOutcome>> _answers = new();

        public byte[]? LastSelfie { get; private set; }

        public void Next(IdentityVerificationOutcome outcome) => _answers.Enqueue(() => outcome);

        public void Fail(Exception error) => _answers.Enqueue(() => throw error);

        public Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct) =>
            Task.FromResult(new CaptureAssessment(true, [], [], true));

        public async Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            await request.Selfie.CopyToAsync(copy, ct);
            LastSelfie = copy.ToArray();
            return _answers.Dequeue()();
        }
    }

    /// <summary>The alerts raised, in order.</summary>
    private sealed class RecordingAlerts : List<(Guid SessionId, string Reason)>, IVerificationOperatorAlerts
    {
        public Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
        {
            Add((sessionId, reasonCode));
            return Task.CompletedTask;
        }
    }

    /// <summary>An MRZ that reads the booker of every stay of the test as a valid, unexpired Italian identity card.</summary>
    private sealed class ReadableMrz : IMrzExtractionService
    {
        public Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            Task.FromResult<MrzExtractionResult?>(Read());

        public Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MrzReading(Read(), []));

        private static MrzExtractionResult Read() => new()
        {
            DocumentType = "IdCard",
            Surname = "TEST",
            GivenNames = "TEST",
            DocumentNumber = "CA00000AA",
            DateOfBirth = new DateTime(1990, 1, 1),
            ExpirationDate = new DateTime(2030, 1, 1),
            IsExpired = false,
            Nationality = "ITA",
            CheckDigitsValid = true,
            Confidence = 0.95,
        };
    }

    /// <summary>A private bucket in memory, counting the writes.</summary>
    private sealed class MemoryStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public int Puts { get; private set; }

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Puts++;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Objects[key] = copy.ToArray();
        }

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.ContainsKey(key));

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string key) => throw new NotSupportedException("The private bucket has no public URL.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(null);
    }

    /// <summary>A logger that keeps every formatted line, to prove what is (not) written.</summary>
    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
