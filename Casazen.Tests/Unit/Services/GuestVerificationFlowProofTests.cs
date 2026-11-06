using Casazen.Core.Authorization;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02/T04 proofs of the guest verification flow: no lock code leaves without a passed verification (AC-NF50-9), nothing of the
/// selfie reaches the database, the storage, a queue or the logs (AC-NF50-10), no selfie without the consent and none for a minor (ADR-013-R9),
/// nothing of the document kept in Ticino (ADR-013-R6), the kept photo deleted on every exit, and the decision of an operator holds for the whole
/// booking, whatever link the guest uses (review fix).
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

    /// <summary>
    /// Privacy rule (ADR-013-R9, AC-NF50-7): no selfie is run without a recorded consent (409 <c>biometric_consent_required</c>), and a refused
    /// consent or a consent without its version is not a consent: the guest is with the operator, or asked again, and the agent never runs.
    /// </summary>
    [Fact]
    public async Task GuestVerificationFlow_SelfieWithoutConsent_IsRefusedAndNothingRuns_Test()
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Hugo", "Villa");
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));

        var withoutConsent = await world.SelfieRawAsync(session);
        Assert.Equal(SelfieStepStatus.ConsentRequired, withoutConsent.Status);
        Assert.Null(withoutConsent.LockCode);
        Assert.Equal(0, world.Agent.Calls);
        Assert.Empty(world.Alerts);
        Assert.Empty(world.Emails);
        Assert.False(await world.Db.BiometricVerificationLogs.AnyAsync());

        // A consent without the version of its text is not recorded.
        var noVersion = await world.Flow().RecordConsentAsync(session, true, "  ", CancellationToken.None);
        Assert.Equal(BiometricConsentStatus.VersionRequired, noVersion.Status);
        Assert.Null(session.BiometricConsentAt);
        Assert.Equal(SelfieStepStatus.ConsentRequired, (await world.SelfieRawAsync(session)).Status);

        // A refusal hands the verification to the operator: the selfie that follows is not run either.
        var refused = await world.Flow().RecordConsentAsync(session, false, null, CancellationToken.None);
        Assert.Equal(BiometricConsentStatus.Refused, refused.Status);
        Assert.NotNull(session.BiometricConsentRefusedAt);
        Assert.Null(session.BiometricConsentAt);
        var afterRefusal = await world.SelfieRawAsync(session);
        Assert.Equal(SelfieStepStatus.Done, afterRefusal.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, afterRefusal.Result);
        Assert.Null(afterRefusal.LockCode);
        Assert.Equal(0, world.Agent.Calls);
        Assert.Equal(VerificationAlertReasons.ConsentRefused, Assert.Single(world.Alerts).Reason);
    }

    /// <summary>
    /// Privacy rule (ADR-013-R9): a minor is never processed, whether the check-in data or the MRZ of the document say so: the answer is
    /// <c>operator</c> (not 400), the agent never runs, nothing is kept, no code is given.
    /// </summary>
    [Fact]
    public async Task BiometricMinor_ExcludedFromProcessing()
    {
        await using var world = await World.CreateAsync();

        // The check-in data say the guest who verifies is a minor: the consent is not even recorded.
        var byData = await world.StayAsync("Ida", "Piccoli", new DateTime(2014, 3, 3));
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        var consent = await world.Flow().RecordConsentAsync(byData, true, "TEST-biometric-v1", CancellationToken.None);
        Assert.Equal(BiometricConsentStatus.Operator, consent.Status);
        Assert.Null(byData.BiometricConsentAt);
        var byDataSelfie = await world.SelfieRawAsync(byData);
        Assert.Equal(SelfieStepStatus.Done, byDataSelfie.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, byDataSelfie.Result);
        Assert.Null(byDataSelfie.LockCode);

        // The check-in data say an adult, the MRZ of the document says a minor: the photo is not kept and the selfie is not run.
        var byDocument = await world.StayAsync("Luca", "Adulti");
        Assert.Equal(
            BiometricConsentStatus.Recorded,
            (await world.Flow().RecordConsentAsync(byDocument, true, "TEST-biometric-v1", CancellationToken.None)).Status);
        world.Mrz.DateOfBirth = new DateTime(2014, 5, 5);
        var read = await world.DocumentAsync(byDocument);
        Assert.False(read.DocumentStored);
        Assert.Empty(world.Storage.Objects);
        Assert.Empty(await world.Db.DocumentImages.ToListAsync());
        var byDocumentSelfie = await world.SelfieRawAsync(byDocument);
        Assert.Equal(SelfieStepStatus.Done, byDocumentSelfie.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, byDocumentSelfie.Result);
        Assert.Null(byDocumentSelfie.LockCode);

        Assert.Equal(0, world.Agent.Calls);
        Assert.Equal(
            [VerificationAlertReasons.MinorExcluded, VerificationAlertReasons.MinorExcluded],
            world.Alerts.Select(a => a.Reason));
        Assert.Empty(world.Emails);
        Assert.False(await world.Db.AccessCodeReleaseLogs.AnyAsync());
        Assert.False(await world.Db.BiometricVerificationLogs.AnyAsync());
    }

    /// <summary>
    /// Privacy rule (ADR-013-R6, R15): a property in Ticino (or in Switzerland with a canton CasaZen does not know) keeps no document: no photo in
    /// the storage, no <see cref="DocumentImage"/> row; the selfie must come with the photo that was read (hash), or it is asked for or refused.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("5192", "TI")]
    public async Task TicinoProperty_DocumentImage_InMemoryOnly(string? municipalityCode, string? canton)
    {
        await using var world = await World.CreateAsync();
        var propertyId = await world.AddPropertyAsync("CH", municipalityCode, canton);
        var session = await world.StayAsync("Carlo", "Ticinesi", propertyId: propertyId);
        Assert.Equal(
            BiometricConsentStatus.Recorded,
            (await world.Flow().RecordConsentAsync(session, true, "TEST-biometric-v1", CancellationToken.None)).Status);
        var photo = TestImages.Png(120, 80, seed: 9);

        var read = await world.DocumentAsync(session, photo);
        Assert.True(read.DocumentNotArchived);
        Assert.False(read.DocumentStored);
        Assert.Equal(0, world.Storage.Puts);
        Assert.Empty(world.Storage.Objects);
        Assert.Empty(await world.Db.DocumentImages.ToListAsync());
        Assert.True((await world.Flow().GetStateAsync(session, CancellationToken.None)).DocumentNotArchived);

        // Without the photo it is asked again; with another photo it is refused; the agent has not run.
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        Assert.Equal(SelfieStepStatus.DocumentPhotoRequired, (await world.SelfieRawAsync(session)).Status);
        var other = await world.SelfieRawAsync(session, documentPhoto: TestImages.Png(120, 80, seed: 10));
        Assert.Equal(SelfieStepStatus.DocumentPhotoMismatch, other.Status);
        Assert.Null(other.LockCode);
        Assert.Equal(0, world.Agent.Calls);

        // The photo that was read: the agent runs and the code is given; the storage has never seen a picture.
        var ok = await world.SelfieAsync(session, documentPhoto: photo);
        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        Assert.Equal(LockCode, ok.LockCode);
        Assert.Equal(1, world.Agent.Calls);
        Assert.Equal(0, world.Storage.Puts);
        Assert.Empty(await world.Db.DocumentImages.ToListAsync());
    }

    /// <summary>
    /// Privacy rule (ADR-013-R2, R6): the photo of the document kept for the comparison is deleted on every way out of the verification, not only
    /// when it passes: a review, the last failed attempt, an error, a refused consent, a request for the operator, a document of a minor.
    /// </summary>
    [Theory]
    [InlineData("review")]
    [InlineData("last-attempt")]
    [InlineData("error")]
    [InlineData("consent-refused")]
    [InlineData("request-operator")]
    [InlineData("minor-by-document")]
    public async Task GuestVerificationFlow_PhotoOfTheDocument_IsDeletedOnEveryExit_Test(string exit)
    {
        await using var world = await World.CreateAsync();
        var session = await world.ReadyStayAsync("Gina", "Neri");
        Assert.Single(world.Storage.Objects);

        switch (exit)
        {
            case "review":
                world.Agent.Next(Outcome(IdentityVerificationResult.Review));
                await world.SelfieAsync(session);
                break;
            case "last-attempt":
                world.Agent.Next(Outcome(IdentityVerificationResult.NotOk, lastAttempt: true));
                await world.SelfieAsync(session);
                break;
            case "error":
                world.Agent.Fail(new InvalidOperationException("model crashed"));
                await world.SelfieAsync(session);
                break;
            case "consent-refused":
                await world.Flow().RecordConsentAsync(session, false, null, CancellationToken.None);
                break;
            case "request-operator":
                await world.Flow().RequestOperatorAsync(session, CancellationToken.None);
                break;
            case "minor-by-document":
                world.Mrz.DateOfBirth = new DateTime(2014, 5, 5);
                await world.DocumentAsync(session);
                await world.SelfieAsync(session);
                break;
        }

        Assert.Empty(world.Storage.Objects);
        Assert.All(await world.Db.DocumentImages.ToListAsync(), image => Assert.NotNull(image.DeletedAt));
        Assert.Empty(world.Emails);
        Assert.False(await world.Db.AccessCodeReleaseLogs.AnyAsync());
    }

    /// <summary>
    /// Trust rule (AC-NF12-7, AC-NF50-4, review fix): the rejection of an operator holds for the booking, not for the session. A new link (the host,
    /// the daily job) gives a new session, and the guest who is rejected finds «operator» in it: the consent is not recorded, the photo is not kept,
    /// the agent does not run, a selfie that would pass opens no code; the verification restarts only when the operator asks for a new photo.
    /// </summary>
    [Fact]
    public async Task GuestVerificationFlow_RejectedByAnOperator_NewLinkOpensNoCode_Test()
    {
        await using var world = await World.CreateAsync();
        var first = await world.ReadyStayAsync("Mara", "Rossetti");
        world.Agent.Next(Outcome(IdentityVerificationResult.Review));
        Assert.Equal(IdentityVerificationGuestResults.Operator, (await world.SelfieAsync(first)).Result);
        var alertsBefore = world.Alerts.Count;
        var callsBefore = world.Agent.Calls;

        // The operator rejects (the note is the operator's), then a new link replaces the session.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await world.Queue().RecordManualVerificationAsync(first.BookingId, world.Scope, false, "Il volto non corrisponde", "auth0|operator"));
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await world.NewLinkAsync(first);
        Assert.Contains(first.BookingId, await VerificationHandOff.BookingsWithOperatorAsync(world.Db, [first.BookingId], CancellationToken.None));

        // In the new session: no consent, no photo kept, no agent, no code, even for a selfie that would pass.
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        var consent = await world.Flow().RecordConsentAsync(second, true, "TEST-biometric-v1", CancellationToken.None);
        Assert.Equal(BiometricConsentStatus.Operator, consent.Status);
        Assert.Null(second.BiometricConsentAt);
        var state = await world.Flow().GetStateAsync(second, CancellationToken.None);
        Assert.Equal(IdentityVerificationSteps.Operator, state.Step);
        Assert.True(state.OperatorRequested);
        var read = await world.DocumentAsync(second);
        Assert.False(read.DocumentStored);
        Assert.Empty(world.Storage.Objects);
        var selfie = await world.SelfieRawAsync(second);
        Assert.Equal(SelfieStepStatus.Done, selfie.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, selfie.Result);
        Assert.Null(selfie.LockCode);
        Assert.Equal(callsBefore, world.Agent.Calls);
        Assert.Empty(world.Emails);
        Assert.False(await world.Db.AccessCodeReleaseLogs.AnyAsync());
        Assert.Null(await world.AccessCodes().GetAccessCodeAsync(first.BookingId, "auth0|owner", world.Scope));

        // The guest asks for a person again after the decision: the operators are told again, and the booking is in the queue once.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Flow().RequestOperatorAsync(second, CancellationToken.None);
        Assert.Equal(alertsBefore + 1, world.Alerts.Count);
        var pending = Assert.Single(await world.Queue().GetPendingVerificationsAsync("auth0|operator", world.Scope));
        Assert.Equal(first.BookingId, pending.BookingId);
    }

    /// <summary>
    /// Trust rule (review fix): a hand-off still open is the booking's too. A host's new link does not lose it from the queue (one entry, the
    /// operators are not told twice) and does not start the verification again; the operator opens the way by asking for a new photo, which writes
    /// an operator row that is not a decision (not counted, the gate stays shut), and the new session then verifies and gets the code, closing the
    /// "To do".
    /// </summary>
    [Fact]
    public async Task GuestVerificationFlow_OpenHandOff_SurvivesANewLinkUntilTheOperatorAsksForANewPhoto_Test()
    {
        await using var world = await World.CreateAsync();
        var first = await world.ReadyStayAsync("Nora", "Galli");
        world.Agent.Next(Outcome(IdentityVerificationResult.Review));
        Assert.Equal(IdentityVerificationGuestResults.Operator, (await world.SelfieAsync(first)).Result);
        Assert.Single(world.Alerts);
        var callsBefore = world.Agent.Calls;

        // The host sends a new link: the old session is expired, the hand-off is still in the queue, once.
        world.Clock.Advance(TimeSpan.FromHours(1));
        var second = await world.NewLinkAsync(first);
        Assert.Equal(first.BookingId, Assert.Single(await world.Queue().GetPendingVerificationsAsync("auth0|operator", world.Scope)).BookingId);

        // The new session finds «operator», and asking for a person again does not alert twice.
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        Assert.Equal(IdentityVerificationGuestResults.Operator, (await world.SelfieAsync(second)).Result);
        Assert.Equal(callsBefore, world.Agent.Calls);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Flow().RequestOperatorAsync(second, CancellationToken.None);
        Assert.Single(world.Alerts);
        Assert.Single(await world.Queue().GetPendingVerificationsAsync("auth0|operator", world.Scope));

        // The operator asks for a new photo: a third link, an operator row that opens the way and is not a decision.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var third = await world.PrepareNewLinkAsync(second);
        var checkIn = new Mock<IGuestCheckInService>();
        checkIn.Setup(s => s.IssueLinkAsync(first.BookingId, world.OrgId))
            .ReturnsAsync(new IssuedCheckInLink(third.Id, "token", Now.UtcDateTime.AddDays(10)));
        Assert.True(await world.Queue(checkIn.Object).RequestNewPhotoAsync(first.BookingId, world.Scope, "auth0|operator"));
        var reopen = await world.Db.BiometricVerificationLogs.SingleAsync(l => l.Method == VerificationMethod.Operator);
        Assert.Equal(BiometricOutcome.Escalate, reopen.Outcome);
        Assert.Equal("auth0|operator", reopen.OperatorUserId);
        Assert.Null(await world.AccessCodes().GetAccessCodeAsync(first.BookingId, "auth0|owner", world.Scope));
        Assert.Empty(await world.Queue().GetPendingVerificationsAsync("auth0|operator", world.Scope));
        var metrics = await world.Queue().GetMetricsAsync(world.Scope, Now.UtcDateTime.AddDays(-1), Now.UtcDateTime.AddDays(1));
        Assert.Equal(0, metrics.OperatorDecisions);
        Assert.False(await VerificationHandOff.IsWithOperatorAsync(world.Db, first.BookingId, CancellationToken.None));

        // In the third session the verification runs again, passes, gives the code and closes the "To do" of the hand-off.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(
            BiometricConsentStatus.Recorded,
            (await world.Flow().RecordConsentAsync(third, true, "TEST-biometric-v1", CancellationToken.None)).Status);
        Assert.True((await world.DocumentAsync(third)).DocumentStored);
        var ok = await world.SelfieAsync(third);
        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        Assert.Equal(LockCode, ok.LockCode);
        Assert.Contains(first.BookingId, world.Alerts.Resolved);
        Assert.Equal(LockCode, await world.AccessCodes().GetAccessCodeAsync(first.BookingId, "auth0|owner", world.Scope));
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
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly Mock<IEmailQueue> _queue = new();

        private World()
        {
            Db = new AppDbContext(_options, null, _dataProtection);
            _queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
                .Callback<string?, EmailContent, string>((to, content, template) => Emails.Add((to, content, template)))
                .Returns(true);
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public HostScope Scope => new(OrgId);

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; } = new(Now);

        public ScriptedAgent Agent { get; } = new();

        public RecordingAlerts Alerts { get; } = new();

        public ReadableMrz Mrz { get; } = new();

        public MemoryStorage Storage { get; } = new();

        public List<(string? To, EmailContent Content, string Template)> Emails { get; } = [];

        public List<string> Logs { get; } = [];

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            world.Db.Orgs.Add(new OrgEntity
            {
                Id = world.OrgId,
                Name = "Agenzia verifiche",
                Slug = $"verifiche-{world.OrgId:N}"[..20],
                DisplayName = "Agenzia verifiche",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Agency,
            });
            world.Db.Properties.Add(world.NewProperty(world._propertyId, "IT", null));
            world.Db.Set<PropertyAccessCode>().Add(world.NewCode(world._propertyId));
            await world.Db.SaveChangesAsync();
            return world;
        }

        public IdentityVerificationFlowService Flow() => new(
            Db,
            Agent,
            Alerts,
            AccessCodes(),
            _queue.Object,
            Mrz,
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(Db, Mock.Of<IAlloggiatiCodeTableService>()),
            Storage,
            _dataProtection,
            Clock,
            new ListLogger<IdentityVerificationFlowService>(Logs));

        public AccessCodeService AccessCodes() => new(Db, new ListLogger<AccessCodeService>(Logs));

        public VerificationQueueService Queue(IGuestCheckInService? checkIn = null) => new(
            Db,
            _queue.Object,
            new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://site.example.test" })),
            checkIn ?? Mock.Of<IGuestCheckInService>(),
            NullLogger<VerificationQueueService>.Instance,
            Clock,
            AccessCodes());

        /// <summary>A property (in another country when said) with a lock code, and the municipality of its canton when there is one.</summary>
        public async Task<Guid> AddPropertyAsync(string country, string? municipalityCode, string? canton)
        {
            var property = NewProperty(Guid.NewGuid(), country, municipalityCode);
            Db.Properties.Add(property);
            Db.Set<PropertyAccessCode>().Add(NewCode(property.Id));
            if (municipalityCode is not null && canton is not null)
                Db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = municipalityCode, CantonCode = canton, OfficialName = "Lugano" });

            await Db.SaveChangesAsync();
            return property.Id;
        }

        /// <summary>A confirmed stay of an adult booker with an open check-in session (loaded as the public endpoint loads it).</summary>
        public async Task<GuestCheckInSession> StayAsync(string firstName, string lastName, DateTime? dateOfBirth = null, Guid? propertyId = null)
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                FirstName = firstName,
                LastName = lastName,
                Email = $"{firstName.ToLowerInvariant()}@example.com",
                DateOfBirth = dateOfBirth ?? new DateTime(1990, 1, 1),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                PropertyId = propertyId ?? _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 12),
                CheckOutDate = new DateTime(2026, 11, 14),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            var session = NewSession(booking.Id);
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            await Db.SaveChangesAsync();

            return await LoadAsync(session.Id);
        }

        /// <summary>A stay whose guest gave the consent and uploaded the document (kept encrypted: the property is not in Ticino).</summary>
        public async Task<GuestCheckInSession> ReadyStayAsync(string firstName, string lastName)
        {
            var session = await StayAsync(firstName, lastName);
            var flow = Flow();
            Assert.Equal(
                BiometricConsentStatus.Recorded,
                (await flow.RecordConsentAsync(session, true, "TEST-biometric-v1", CancellationToken.None)).Status);
            var read = await DocumentAsync(session);
            Assert.True(read.DocumentStored);
            return session;
        }

        /// <summary>A new link of the booking: the open session is expired, a new one replaces it (loaded as the public endpoint loads it).</summary>
        public async Task<GuestCheckInSession> NewLinkAsync(GuestCheckInSession previous)
        {
            var added = await PrepareNewLinkAsync(previous);
            return await LoadAsync(added.Id);
        }

        /// <summary>The session a new link would add, written (the previous one expired), for a fake <c>IssueLinkAsync</c> to hand back.</summary>
        public async Task<GuestCheckInSession> PrepareNewLinkAsync(GuestCheckInSession previous)
        {
            var open = await Db.GuestCheckInSessions.Where(s => s.BookingId == previous.BookingId && s.Status != GuestCheckInSessionStatus.Scaduto).ToListAsync();
            foreach (var session in open)
                session.Status = GuestCheckInSessionStatus.Scaduto;

            var added = NewSession(previous.BookingId);
            Db.GuestCheckInSessions.Add(added);
            await Db.SaveChangesAsync();
            return added;
        }

        public async Task<DocumentStepResult> DocumentAsync(GuestCheckInSession session, byte[]? image = null)
        {
            using var document = new MemoryStream(image ?? TestImages.Png(120, 80, seed: 3));
            return await Flow().SubmitDocumentAsync(session, document, CancellationToken.None);
        }

        public async Task<SelfieStepResult> SelfieRawAsync(GuestCheckInSession session, byte[]? selfie = null, byte[]? documentPhoto = null)
        {
            using var stream = new MemoryStream(selfie ?? TestImages.Png(96, 96, seed: 5));
            using var photo = documentPhoto is null ? null : new MemoryStream(documentPhoto);
            return await Flow().SubmitSelfieAsync(session, stream, photo, CancellationToken.None);
        }

        public async Task<SelfieStepResult> SelfieAsync(GuestCheckInSession session, byte[]? selfie = null, byte[]? documentPhoto = null)
        {
            var result = await SelfieRawAsync(session, selfie, documentPhoto);
            Assert.Equal(SelfieStepStatus.Done, result.Status);
            return result;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();

        private Property NewProperty(Guid id, string country, string? municipalityCode) => new()
        {
            Id = id,
            OrgId = OrgId,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = country == "CH" ? "Lugano" : "Roma",
            PostalCode = "00100",
            Country = country,
            SwissMunicipalityCode = municipalityCode,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };

        private PropertyAccessCode NewCode(Guid propertyId) => new()
        {
            OrgId = OrgId,
            PropertyId = propertyId,
            EncryptedCode = LockCode,
            UpdatedAt = Now.UtcDateTime,
        };

        private GuestCheckInSession NewSession(Guid bookingId) => new()
        {
            BookingId = bookingId,
            OrgId = OrgId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = Now.UtcDateTime.AddDays(10),
            Status = GuestCheckInSessionStatus.InCompilazione,
            Language = "it",
            CreatedAt = Clock.GetUtcNow().UtcDateTime,
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
        };

        private async Task<GuestCheckInSession> LoadAsync(Guid sessionId) =>
            await Db.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .SingleAsync(s => s.Id == sessionId);
    }

    /// <summary>An agent that answers a script, accepts every capture and keeps the last selfie it read (to prove what reached it).</summary>
    private sealed class ScriptedAgent : IIdentityVerificationAgent
    {
        private readonly Queue<Func<IdentityVerificationOutcome>> _answers = new();

        public byte[]? LastSelfie { get; private set; }

        /// <summary>How many times a verification ran (the selfie reached the agent).</summary>
        public int Calls { get; private set; }

        public void Next(IdentityVerificationOutcome outcome) => _answers.Enqueue(() => outcome);

        public void Fail(Exception error) => _answers.Enqueue(() => throw error);

        public Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct) =>
            Task.FromResult(new CaptureAssessment(true, [], [], true));

        public async Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct)
        {
            Calls++;
            using var copy = new MemoryStream();
            await request.Selfie.CopyToAsync(copy, ct);
            LastSelfie = copy.ToArray();
            return _answers.Dequeue()();
        }
    }

    /// <summary>The alerts raised, in order, and the "To do" items closed.</summary>
    private sealed class RecordingAlerts : List<(Guid SessionId, string Reason)>, IVerificationOperatorAlerts
    {
        public List<Guid> Resolved { get; } = [];

        public Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
        {
            Add((sessionId, reasonCode));
            return Task.CompletedTask;
        }

        public Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct)
        {
            Resolved.Add(bookingId);
            return Task.CompletedTask;
        }
    }

    /// <summary>An MRZ that reads the booker of every stay of the test as a valid, unexpired Italian identity card (born on <see cref="DateOfBirth"/>).</summary>
    private sealed class ReadableMrz : IMrzExtractionService
    {
        public DateTime DateOfBirth { get; set; } = new(1990, 1, 1);

        public Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            Task.FromResult<MrzExtractionResult?>(Read());

        public Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MrzReading(Read(), []));

        private MrzExtractionResult Read() => new()
        {
            DocumentType = "IdCard",
            Surname = "TEST",
            GivenNames = "TEST",
            DocumentNumber = "CA00000AA",
            DateOfBirth = DateOfBirth,
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
