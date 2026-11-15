using System.Reflection;
using System.Security.Claims;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.External.Sdi;
using Casazen.Web.Authorization;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// E17/F01/US02/T01 and T02 (NF-116): the endpoints of the SDI states and the webhook of the channel, and the job that is its fallback. The webhook refuses a wrong secret before it reads or records
/// anything (and never logs the secret or the body); the changes are behind both flags and <c>payment.write</c> and reach only the documents the caller reaches; the job writes only with the key and the flags.
/// </summary>
public class SdiControllersTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly string Secret = new('s', 40);
    private const string Body = """{"Endpoint":"/v1/update","Method":"POST","ResourceId":9,"Success":true}""";

    // ---------------------------------------------------------------- the webhook

    /// <summary>
    /// PROOF TEST: permission rule. A call with a wrong secret (or none) is refused with 401 before anything is read or recorded: the provider is not asked, no document is looked for, and neither the
    /// secret nor the body reaches the log. A missing or short secret, or no provider, is 500: never an accepted call.
    /// </summary>
    [Fact]
    public async Task SdiWebhook_WrongSecretRefused_Test()
    {
        var provider = ConfiguredProvider();
        var sdi = new Mock<IFiscalDocumentSdiService>();
        var logger = new CapturingLogger<SdiWebhookController>();
        var controller = Webhook(provider.Object, sdi.Object, logger: logger);

        foreach (var wrong in new[] { new string('x', 40), Secret + "x", Secret[..39], string.Empty, new string('s', 1000) })
        {
            var refused = await controller.Receive(wrong, CancellationToken.None);

            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(refused).StatusCode);
        }

        provider.Verify(p => p.ReadNotificationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        sdi.VerifyNoOtherCalls();
        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain("ResourceId", line, StringComparison.Ordinal);
        });

        // No usable secret configured (empty, too short) or no provider: nothing is accepted, whatever the call says.
        foreach (var configured in new[] { string.Empty, new string('s', 31) })
        {
            var notConfigured = Webhook(provider.Object, sdi.Object, secret: configured);
            Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(await notConfigured.Receive(configured, CancellationToken.None)).StatusCode);
        }

        var noProvider = Webhook(new Mock<ISdiEInvoiceProvider>().Object, sdi.Object);
        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(await noProvider.Receive(Secret, CancellationToken.None)).StatusCode);
        provider.Verify(p => p.ReadNotificationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SdiWebhook_TheRightSecret_RecordsTheOutcomeUnderTheOrgOfTheDocument()
    {
        var documentId = Guid.NewGuid();
        var provider = ConfiguredProvider();
        provider.Setup(p => p.ReadNotificationAsync(Body, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SdiNotification("42", new SdiStatusResult(SdiOutcomeKind.Rejected, "00417", "words"))]);
        var sdi = new Mock<IFiscalDocumentSdiService>();
        sdi.Setup(s => s.FindByTransmissionAsync("42", It.IsAny<CancellationToken>())).ReturnsAsync(new SdiSubmittedDocument(OrgId, documentId, "42"));
        sdi.Setup(s => s.RecordOutcomeAsync(documentId, It.IsAny<SdiOutcome>(), It.IsAny<CancellationToken>())).ReturnsAsync(SdiRecordResult.Recorded);
        var entered = new List<Guid>();

        var answer = await Webhook(provider.Object, sdi.Object, entered: entered).Receive(Secret, CancellationToken.None);

        Assert.IsType<OkResult>(answer);
        Assert.Equal([OrgId], entered);
        sdi.Verify(s => s.RecordOutcomeAsync(documentId, new SdiOutcome(SdiOutcomeKind.Rejected, "00417", "words"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SdiWebhook_ASubmissionNotKnownYet_IsRetriedByTheProvider_AndACallWithNothingToRecordIsAcknowledged()
    {
        var provider = ConfiguredProvider();
        var sdi = new Mock<IFiscalDocumentSdiService>();
        sdi.Setup(s => s.FindByTransmissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((SdiSubmittedDocument?)null);

        provider.Setup(p => p.ReadNotificationAsync(Body, It.IsAny<CancellationToken>())).ReturnsAsync([new SdiNotification("42", new SdiStatusResult(SdiOutcomeKind.Delivered))]);
        var unknown = await Webhook(provider.Object, sdi.Object).Receive(Secret, CancellationToken.None);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(unknown).StatusCode);
        sdi.Verify(s => s.RecordOutcomeAsync(It.IsAny<Guid>(), It.IsAny<SdiOutcome>(), It.IsAny<CancellationToken>()), Times.Never);

        provider.Setup(p => p.ReadNotificationAsync(Body, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Assert.IsType<OkResult>(await Webhook(provider.Object, sdi.Object).Receive(Secret, CancellationToken.None));
    }

    [Fact]
    public async Task SdiWebhook_AFailureOfTheProviderOrTheDatabase_IsNotSwallowed_SoTheProviderCallsAgain()
    {
        var provider = ConfiguredProvider();
        provider.Setup(p => p.ReadNotificationAsync(Body, It.IsAny<CancellationToken>())).ThrowsAsync(new SdiProviderException("update", 503));

        await Assert.ThrowsAsync<SdiProviderException>(() => Webhook(provider.Object, new Mock<IFiscalDocumentSdiService>().Object).Receive(Secret, CancellationToken.None));
    }

    [Fact]
    public void SdiWebhook_IsAnonymousAndBehindBothFlags()
    {
        var type = typeof(SdiWebhookController);

        Assert.NotNull(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(
            [FeatureFlags.FiscalDocumentIssuance, FeatureFlags.SdiSubmission],
            type.GetCustomAttributes<FeatureGateAttribute>().Select(a => a.Flag).Order().ToArray());
        Assert.Equal("api/webhooks/sdi/{secret}", type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    // ---------------------------------------------------------------- the endpoints of a document

    [Fact]
    public void SdiEndpoints_TheChangesAreBehindBothFlagsAndPaymentWrite_TheStateIsARead()
    {
        var type = typeof(FiscalDocumentSdiController);
        Assert.Equal(CasazenPolicies.PaymentRead, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);

        foreach (var action in new[] { nameof(FiscalDocumentSdiController.Submit), nameof(FiscalDocumentSdiController.Declare), nameof(FiscalDocumentSdiController.Correct) })
        {
            var method = type.GetMethod(action)!;
            Assert.Equal(CasazenPolicies.PaymentWrite, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
            Assert.Equal(
                [FeatureFlags.FiscalDocumentIssuance, FeatureFlags.SdiSubmission],
                method.GetCustomAttributes<FeatureGateAttribute>().Select(a => a.Flag).Order().ToArray());
        }

        Assert.Empty(type.GetMethod(nameof(FiscalDocumentSdiController.Get))!.GetCustomAttributes<FeatureGateAttribute>());
        Assert.Equal([FeatureFlags.FiscalDocumentIssuance], type.GetMethod(nameof(FiscalDocumentSdiController.Issuers))!.GetCustomAttributes<FeatureGateAttribute>().Select(a => a.Flag).ToArray());
    }

    [Fact]
    public async Task SdiEndpoints_ADocumentTheCallerDoesNotReach_IsNotFound_AndNothingIsDone()
    {
        var documentId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var sdi = new Mock<IFiscalDocumentSdiService>();
        var corrections = new Mock<IFiscalDocumentCorrectionService>();
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.GetAsync(documentId, It.IsAny<CancellationToken>())).ReturnsAsync(new FiscalDocument { Id = documentId, OrgId = OrgId, BookingId = bookingId });
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", Guid.NewGuid()));

        var denied = Documents(documents.Object, sdi.Object, corrections.Object, resources.Object, allowed: false);
        Assert.All(
            [
                (await denied.Get(documentId)).Result,
                (await denied.Submit(documentId)).Result,
                (await denied.Declare(documentId, new DeclareFiscalDocumentSdiRequest { Kind = SdiDeclarationKind.Sent })).Result,
                (await denied.Correct(documentId)).Result,
            ],
            result => Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>(result).StatusCode));
        sdi.VerifyNoOtherCalls();
        corrections.VerifyNoOtherCalls();

        // An unknown document is the same answer.
        var missing = Documents(documents.Object, sdi.Object, corrections.Object, resources.Object, allowed: true);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsType<ObjectResult>((await missing.Submit(Guid.NewGuid())).Result).StatusCode);
        sdi.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SdiEndpoints_ACallerWhoReachesTheDocument_SubmitsDeclaresAndCorrectsIt_AndReadsTheReasonInPlainWords()
    {
        var documentId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var state = new FiscalDocumentSdiState(documentId, FiscalDocumentSdiStatuses.Rejected, "00417", null, null, true, false, false, true);
        var sdi = new Mock<IFiscalDocumentSdiService>();
        sdi.Setup(s => s.SubmitAsync(documentId, It.IsAny<CancellationToken>())).ReturnsAsync(new SdiSubmitResult(SdiSubmitOutcome.NotConfigured, FiscalDocumentSdiStatuses.ToSend));
        sdi.Setup(s => s.GetStateAsync(documentId, It.IsAny<CancellationToken>())).ReturnsAsync(state);
        sdi.Setup(s => s.DeclareManuallyAsync(documentId, It.IsAny<SdiDeclaration>(), It.IsAny<CancellationToken>())).ReturnsAsync(state);
        var replacement = new FiscalDocument { Id = Guid.NewGuid(), OrgId = OrgId, ReplacesDocumentId = documentId, Series = "2026", SequenceNumber = 1, Number = "2026/1" };
        var corrections = new Mock<IFiscalDocumentCorrectionService>();
        corrections.Setup(c => c.StartCorrectionAsync(documentId, "auth0|collaborator", It.IsAny<CancellationToken>())).ReturnsAsync(replacement);
        var documents = new Mock<IFiscalDocumentService>();
        documents.Setup(d => d.GetAsync(documentId, It.IsAny<CancellationToken>())).ReturnsAsync(new FiscalDocument { Id = documentId, OrgId = OrgId, BookingId = bookingId });
        var resources = new Mock<IHostResourceLookup>();
        resources.Setup(r => r.ForBookingAsync(bookingId, It.IsAny<CancellationToken>())).ReturnsAsync(new HostResource(OrgId, "auth0|owner", Guid.NewGuid()));
        var controller = Documents(documents.Object, sdi.Object, corrections.Object, resources.Object, allowed: true);

        var read = Assert.IsType<OkObjectResult>((await controller.Get(documentId)).Result);
        var dto = Assert.IsType<FiscalDocumentSdiDto>(read.Value);
        Assert.Equal(("rejected", "00417", true), (dto.Status, dto.ErrorCode, dto.CanCorrect));
        Assert.Contains("00417", dto.ErrorCode, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(dto.Reason));
        Assert.DoesNotContain("SdiReason", dto.Reason, StringComparison.Ordinal);

        var submitted = Assert.IsType<OkObjectResult>((await controller.Submit(documentId)).Result);
        Assert.Equal("NotConfigured", Assert.IsType<SubmitFiscalDocumentSdiResultDto>(submitted.Value).Outcome);

        Assert.IsType<OkObjectResult>((await controller.Declare(documentId, new DeclareFiscalDocumentSdiRequest { Kind = SdiDeclarationKind.Rejected, ErrorCode = "417" })).Result);
        sdi.Verify(s => s.DeclareManuallyAsync(documentId, new SdiDeclaration(SdiDeclarationKind.Rejected, null, null, "417", null), It.IsAny<CancellationToken>()), Times.Once);

        var created = Assert.IsType<CreatedAtActionResult>((await controller.Correct(documentId)).Result);
        var draft = Assert.IsType<FiscalDocumentDto>(created.Value);
        Assert.Equal((replacement.Id, documentId, "2026/1"), (draft.Id, draft.ReplacesDocumentId, draft.Number));
    }

    /// <summary>
    /// PROOF TEST: permission rule (the scope of the caller). The issuers of the channel are read through the scope the caller has (a collaborator «Solo alcuni» gets the restricted one, never the
    /// whole org's), and a caller with no scope (deactivated, another org) is refused before anything is read.
    /// </summary>
    [Fact]
    public async Task SdiIssuers_AreReadThroughTheScopeOfTheCaller_AndACallerWithNoScopeIsRefused()
    {
        var issuerId = Guid.NewGuid();
        var restricted = new HostScope(OrgId, GrantedToUserId: "auth0|collaborator");
        var sdi = new Mock<IFiscalDocumentSdiService>();
        sdi.Setup(s => s.IssuerRegistrationsAsync(restricted, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SdiIssuerRegistration(issuerId, null, SdiIssuerRegistrationStates.Pending)]);
        var scopes = new Mock<IHostScopeResolver>();
        scopes
            .Setup(r => r.ResolveAsync("auth0|collaborator", It.IsAny<IReadOnlySet<string>>(), OrgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restricted);

        var served = await Documents(Mock.Of<IFiscalDocumentService>(), sdi.Object, Mock.Of<IFiscalDocumentCorrectionService>(), Mock.Of<IHostResourceLookup>(), allowed: true, scopes.Object).Issuers();

        var dto = Assert.IsType<SdiIssuersDto>(Assert.IsType<OkObjectResult>(served.Result).Value);
        Assert.True(dto.ProviderConfigured);
        Assert.Equal([(issuerId, "pending")], dto.Issuers.Select(i => (i.IssuerProfileId, i.State)).ToList());
        sdi.Verify(s => s.IssuerRegistrationsAsync(restricted, It.IsAny<CancellationToken>()), Times.Once);

        // No scope at all: refused, and the service is never asked.
        var untouched = new Mock<IFiscalDocumentSdiService>();
        var nobody = Documents(Mock.Of<IFiscalDocumentService>(), untouched.Object, Mock.Of<IFiscalDocumentCorrectionService>(), Mock.Of<IHostResourceLookup>(), allowed: true, Mock.Of<IHostScopeResolver>());
        Assert.IsType<UnauthorizedResult>((await nobody.Issuers()).Result);
        untouched.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- the reason in plain words

    [Fact]
    public void Dto_ARejectedDocumentAlwaysSaysWhy_AStillToSendOneOnlyWhenTheChannelRefused()
    {
        var texts = new ResxDocumentTexts();
        var id = Guid.NewGuid();

        var rejected = FiscalDocumentSdiDto.From(new FiscalDocumentSdiState(id, "rejected", "00417", null, null, true, false, false, true), texts, "it");
        Assert.Contains("partita IVA", rejected.Reason, StringComparison.Ordinal);
        var english = FiscalDocumentSdiDto.From(new FiscalDocumentSdiState(id, "rejected", "00417", null, null, true, false, false, true), texts, "en");
        Assert.Contains("VAT number", english.Reason, StringComparison.Ordinal);

        // A code nobody knows is the generic words with the raw code; a rejection without a code says so.
        Assert.Contains("99999", FiscalDocumentSdiDto.From(new FiscalDocumentSdiState(id, "rejected", "99999", null, null, true, false, false, true), texts, "it").Reason, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(FiscalDocumentSdiDto.From(new FiscalDocumentSdiState(id, "rejected", null, null, null, true, false, false, true), texts, "it").Reason));

        Assert.Null(FiscalDocumentSdiDto.From(new FiscalDocumentSdiState(id, "to_send", null, null, null, true, true, true, false), texts, "it").Reason);
        Assert.Contains("canale di invio", FiscalDocumentSdiDto.From(new FiscalDocumentSdiState(id, "to_send", SdiSubmissionErrors.ProviderError, null, null, true, true, true, false), texts, "it").Reason, StringComparison.Ordinal);
        Assert.Null(FiscalDocumentSdiDto.From(new FiscalDocumentSdiState(id, "delivered", null, null, null, true, false, false, false), texts, "it").Reason);
    }

    // ---------------------------------------------------------------- the job

    [Fact]
    public async Task Job_DoesNothingWithoutTheFlagsOrTheProvider_AndWritesOnlyWithTheKeyAndTheFlags()
    {
        var sdi = new Mock<IFiscalDocumentSdiService>();
        var provider = ConfiguredProvider();
        var entered = new List<Guid>();
        await using var services = JobServices(sdi.Object, provider.Object, entered);

        // The flags off (either of them), or no provider: nothing is asked and nothing is written.
        foreach (var flags in new[] { new TestFlags(), new TestFlags(FeatureFlags.FiscalDocumentIssuance), new TestFlags(FeatureFlags.SdiSubmission) })
            await Job(services, flags).ExecuteAsync(CancellationToken.None);
        await using (var unconfigured = JobServices(sdi.Object, new Mock<ISdiEInvoiceProvider>().Object, entered))
            await Job(unconfigured, new TestFlags(FeatureFlags.FiscalDocumentIssuance, FeatureFlags.SdiSubmission)).ExecuteAsync(CancellationToken.None);

        sdi.VerifyNoOtherCalls();
        provider.Verify(p => p.GetStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(entered);
    }

    [Fact]
    public async Task Job_RecordsWhatTheSdiAnswered_UnderTheOrgOfEachDocument_AndFailsAtTheEndWhenSomeCouldNotBeRead()
    {
        var delivered = new SdiSubmittedDocument(Guid.NewGuid(), Guid.NewGuid(), "1");
        var rejected = new SdiSubmittedDocument(Guid.NewGuid(), Guid.NewGuid(), "2");
        var waiting = new SdiSubmittedDocument(Guid.NewGuid(), Guid.NewGuid(), "3");
        var broken = new SdiSubmittedDocument(Guid.NewGuid(), Guid.NewGuid(), "4");
        var last = new SdiSubmittedDocument(Guid.NewGuid(), Guid.NewGuid(), "5");
        var sdi = new Mock<IFiscalDocumentSdiService>();
        sdi.Setup(s => s.SubmittedDocumentsAsync(SdiStatusPollJob.MaxDocumentsPerRun, It.IsAny<CancellationToken>())).ReturnsAsync([delivered, rejected, waiting, broken, last]);
        sdi.Setup(s => s.RecordOutcomeAsync(It.IsAny<Guid>(), It.IsAny<SdiOutcome>(), It.IsAny<CancellationToken>())).ReturnsAsync(SdiRecordResult.Recorded);
        var provider = ConfiguredProvider();
        provider.Setup(p => p.GetStatusAsync("1", It.IsAny<CancellationToken>())).ReturnsAsync(new SdiStatusResult(SdiOutcomeKind.Delivered));
        provider.Setup(p => p.GetStatusAsync("2", It.IsAny<CancellationToken>())).ReturnsAsync(new SdiStatusResult(SdiOutcomeKind.Rejected, "00417", "words"));
        provider.Setup(p => p.GetStatusAsync("3", It.IsAny<CancellationToken>())).ReturnsAsync(new SdiStatusResult(SdiOutcomeKind.Submitted));
        provider.Setup(p => p.GetStatusAsync("4", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        provider.Setup(p => p.GetStatusAsync("5", It.IsAny<CancellationToken>())).ReturnsAsync(new SdiStatusResult(SdiOutcomeKind.Delivered));
        var entered = new List<Guid>();
        await using var services = JobServices(sdi.Object, provider.Object, entered);

        var failed = await Assert.ThrowsAsync<SdiStatusPollException>(() => Job(services, new TestFlags(FeatureFlags.FiscalDocumentIssuance, FeatureFlags.SdiSubmission)).ExecuteAsync(CancellationToken.None));

        // The run goes on after the document it could not read, records the others, and is a failed run.
        Assert.Contains("1 of 5", failed.Message, StringComparison.Ordinal);
        Assert.Equal([delivered.OrgId, rejected.OrgId, waiting.OrgId, broken.OrgId, last.OrgId], entered);
        sdi.Verify(s => s.RecordOutcomeAsync(delivered.DocumentId, new SdiOutcome(SdiOutcomeKind.Delivered), It.IsAny<CancellationToken>()), Times.Once);
        sdi.Verify(s => s.RecordOutcomeAsync(rejected.DocumentId, new SdiOutcome(SdiOutcomeKind.Rejected, "00417", "words"), It.IsAny<CancellationToken>()), Times.Once);
        sdi.Verify(s => s.RecordOutcomeAsync(last.DocumentId, new SdiOutcome(SdiOutcomeKind.Delivered), It.IsAny<CancellationToken>()), Times.Once);
        sdi.Verify(s => s.RecordOutcomeAsync(waiting.DocumentId, It.IsAny<SdiOutcome>(), It.IsAny<CancellationToken>()), Times.Never);
        sdi.Verify(s => s.RecordOutcomeAsync(broken.DocumentId, It.IsAny<SdiOutcome>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Job_IsRegisteredOnlyWhileBothFlagsAreOn_EveryThirtyMinutes_AndNeverRunsTwiceAtOnce()
    {
        Assert.Equal("fiscal-sdi-status", SdiStatusPollJob.RecurringJobId);

        foreach (var (issuance, sdi, registered) in new[] { (true, true, true), (true, false, false), (false, true, false), (false, false, false) })
        {
            var jobs = new Dictionary<string, (Job Job, string Cron, RecurringJobOptions Options)>();
            var manager = new Mock<IRecurringJobManager>();
            manager.Setup(m => m.AddOrUpdate(It.IsAny<string>(), It.IsAny<Job>(), It.IsAny<string>(), It.IsAny<RecurringJobOptions>()))
                .Callback<string, Job, string, RecurringJobOptions>((id, job, cron, options) => jobs[id] = (job, cron, options));
            var flags = new Mock<IFeatureFlags>();
            flags.Setup(f => f.IsEnabled(FeatureFlags.FiscalDocumentIssuance)).Returns(issuance);
            flags.Setup(f => f.IsEnabled(FeatureFlags.SdiSubmission)).Returns(sdi);

            RecurringJobsRegistration.Configure(manager.Object, flags.Object);

            Assert.Equal(registered, jobs.ContainsKey(SdiStatusPollJob.RecurringJobId));
            manager.Verify(m => m.RemoveIfExists(SdiStatusPollJob.RecurringJobId), registered ? Times.Never : Times.Once);
            if (registered)
            {
                var job = jobs[SdiStatusPollJob.RecurringJobId];
                Assert.Equal(("*/30 * * * *", TimeZoneInfo.Utc, typeof(SdiStatusPollJob)), (job.Cron, job.Options.TimeZone, job.Job.Type));
                Assert.NotNull(job.Job.Method.GetCustomAttribute<DisableConcurrentExecutionAttribute>());
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Mock<ISdiEInvoiceProvider> ConfiguredProvider()
    {
        var provider = new Mock<ISdiEInvoiceProvider>();
        provider.SetupGet(p => p.IsConfigured).Returns(true);
        provider.SetupGet(p => p.Name).Returns("test");
        return provider;
    }

    private static SdiWebhookController Webhook(
        ISdiEInvoiceProvider provider,
        IFiscalDocumentSdiService sdi,
        string? secret = null,
        List<Guid>? entered = null,
        ILogger<SdiWebhookController>? logger = null)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => sdi)
            .AddScoped<IBackgroundTenantScope>(_ => new RecordingScope(entered ?? []))
            .BuildServiceProvider();
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        var requestServices = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = requestServices };
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Body));
        var verifier = new SdiWebhookVerifier(Options.Create(new SdiProviderOptions { Name = "invoicetronic", ApiKey = "key", WebhookSecret = secret ?? Secret }));
        return new SdiWebhookController(verifier, provider, services.GetRequiredService<IServiceScopeFactory>(), logger ?? NullLogger<SdiWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static FiscalDocumentSdiController Documents(
        IFiscalDocumentService documents,
        IFiscalDocumentSdiService sdi,
        IFiscalDocumentCorrectionService corrections,
        IHostResourceLookup resources,
        bool allowed,
        IHostScopeResolver? scopes = null)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<IEnumerable<IAuthorizationRequirement>>()))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        var requestServices = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|collaborator"), new Claim(ClaimTypes.NameIdentifier, "auth0|collaborator")], "test");
        return new FiscalDocumentSdiController(
            documents,
            sdi,
            corrections,
            ConfiguredProvider().Object,
            new ResxDocumentTexts(),
            resources,
            authorization.Object,
            org.Object,
            scopes ?? Mock.Of<IHostScopeResolver>(),
            NullLogger<FiscalDocumentSdiController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = requestServices },
            },
        };
    }

    private static ServiceProvider JobServices(IFiscalDocumentSdiService sdi, ISdiEInvoiceProvider provider, List<Guid> entered) =>
        new ServiceCollection()
            .AddScoped(_ => sdi)
            .AddScoped(_ => provider)
            .AddScoped<IBackgroundTenantScope>(_ => new RecordingScope(entered))
            .BuildServiceProvider();

    private static SdiStatusPollJob Job(ServiceProvider services, IFeatureFlags flags) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), flags, NullLogger<SdiStatusPollJob>.Instance);

    private sealed class RecordingScope(List<Guid> entered) : IBackgroundTenantScope
    {
        public void EnterOrg(Guid orgId) => entered.Add(orgId);
    }

    private sealed class TestFlags(params string[] enabled) : IFeatureFlags
    {
        public bool IsEnabled(string flag) => enabled.Contains(flag);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
