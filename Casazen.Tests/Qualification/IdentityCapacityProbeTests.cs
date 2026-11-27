using System.Runtime.InteropServices;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Qualification;

// E74/F02/US01/T03 (NF-198, D12): the tests of the latency and capacity probe. The smoke tests RUN IN CI, over the real models of the build output and the published configuration, like
// the smoke test of the qualification: a request completes with every stage measured, the report holds numbers and the description of the machine and nothing else, the probe registers no
// service of its own, and a pipeline that is too slow, or a budget that is not valid, sends the guest to a person and never approves. The real run is opt-in (CapacityFact).

/// <summary>The pipeline of the probe, built once for the tests of the class (the models are loaded once), and the report of one small run that several tests read.</summary>
public sealed class CapacityProbeFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CapacityReport? _small;

    /// <summary>The pipeline of the qualification with the detector of the probe. The published configuration only, unless the real run is asked for (then the variables of the environment count too).</summary>
    public QualificationPipeline Pipeline { get; private set; } = null!;

    /// <summary>The detector of the probe: the tests make it stall to stand for a machine that is too slow.</summary>
    public ProbeFaceDetector Detector { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var real = CapacityEnvironment.IsOn(CapacityEnvironment.Read(CapacityEnvironment.ProbeVariable));
        (Pipeline, Detector) = await IdentityCapacityProbe.CreatePipelineAsync(includeEnvironmentVariables: real);
    }

    public async Task DisposeAsync()
    {
        if (Pipeline is not null)
            await Pipeline.DisposeAsync();

        _gate.Dispose();
    }

    /// <summary>The report of a small run: two requests at one at a time and at two at once. Run once, the tests that read it share it.</summary>
    public async Task<CapacityReport> SmallRunAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return _small ??= await IdentityCapacityProbe.RunAsync(
                Pipeline,
                new CapacityProbeOptions { Iterations = 2, Concurrency = [1, 2], WarmupIterations = 0 });
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>The probe of the latency and the capacity of the presence pipeline (see the comments at the top of the file).</summary>
public sealed class IdentityCapacityProbeTests(CapacityProbeFixture fixture) : IClassFixture<CapacityProbeFixture>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task IdentityCapacityProbe_OneIteration_RunsEveryStageOfThePipeline_AndCompletes()
    {
        var report = await IdentityCapacityProbe.RunAsync(
            fixture.Pipeline,
            new CapacityProbeOptions { Iterations = 1, Concurrency = [1], WarmupIterations = 0 },
            Ct);

        var level = Assert.Single(report.Levels);
        Assert.Equal((1, 1), (level.Concurrency, level.Requests));
        foreach (var stage in new[] { QualificationStages.PresenceIntake, QualificationStages.PresenceValidation, QualificationStages.PresenceLiveness, QualificationStages.PresenceMatch })
            Assert.Contains(level.Stages, measured => measured.Stage == stage && measured.Count == 1);

        // One request: its p50, its p95 and its largest time are the same number, and the whole is at least as long as each stage.
        Assert.All(
            level.Stages.Append(level.Total),
            measured =>
            {
                Assert.True(measured.P50Ms > 0, measured.Stage);
                Assert.Equal(measured.P50Ms, measured.P95Ms);
                Assert.Equal(measured.P50Ms, measured.MaxMs);
            });
        Assert.Equal(IdentityCapacityProbe.TotalStage, level.Total.Stage);
        Assert.All(level.Stages, measured => Assert.True(measured.MaxMs <= level.Total.MaxMs, measured.Stage));
        Assert.True(level.CpuMsPerRequest > 0);
        Assert.True(level.PeakWorkingSetMb > 0);
        Assert.True(level.AllocatedMbPerRequest > 0);
        Assert.InRange(level.OverTheLimit, 0, 1);

        // What the request carried is what the policy and the limits of the application give: the frames of the challenge at the longest side the intake accepts.
        var policy = fixture.Pipeline.ActivePolicy;
        var challenge = policy.Presence.Challenge;
        var interval = (long)Math.Round(1000.0 / policy.Presence.Sequence.TargetFps);
        Assert.Equal((int)((challenge.LeadInMs + ((long)challenge.Steps * challenge.StepMs)) / interval) + 1, report.Scene.Frames);
        Assert.Equal(fixture.Pipeline.IdentityOptions.Presence.MaxFrameSide, report.Scene.FrameWidth);
        Assert.True(report.Scene.FrameBytes <= fixture.Pipeline.IdentityOptions.Presence.MaxFrameBytes);

        // The limit a request is counted against is the one of the application, not the widened one the qualification works with.
        Assert.InRange(report.Scene.SequenceTimeoutMs, 1, QualificationPipeline.WidenedSequenceTimeoutMs - 1);
    }

    [Fact]
    public async Task IdentityCapacityProbe_EveryLevelOfConcurrency_IsRunAndReported()
    {
        var report = await fixture.SmallRunAsync();

        Assert.Equal([1, 2], report.Levels.Select(level => level.Concurrency));
        Assert.All(report.Levels, level =>
        {
            Assert.Equal(2, level.Requests);
            Assert.True(level.WallSeconds > 0);
            Assert.True(level.RequestsPerSecond > 0);
            Assert.Equal(2, level.Total.Count);
            Assert.True(level.Total.P50Ms <= level.Total.P95Ms && level.Total.P95Ms <= level.Total.MaxMs);
            Assert.True(level.Stages.Count >= 4);
        });
        Assert.Equal(CapacityReport.SchemaName, report.Schema);
    }

    /// <summary>Proof (privacy, ADR-013-R2, R17): the report holds numbers, codes and the description of the machine; no name of the host or of the user, no folder, no picture.</summary>
    [Fact]
    public async Task IdentityCapacityProbe_Report_HoldsNoHostNameAndNoImageData()
    {
        var report = await fixture.SmallRunAsync();
        var json = report.ToJson();
        var markdown = report.ToMarkdown();

        // The machine is described by the number of its processors, its system, its runtime and its architecture: that is all the record has.
        Assert.Equal(
            ["Architecture", "Framework", "ModelThreads", "OperatingSystem", "ProcessorCount"],
            typeof(CapacityMachine).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(Environment.ProcessorCount, document.RootElement.GetProperty("machine").GetProperty("processorCount").GetInt32());
            Assert.DoesNotContain(PropertyNames(document.RootElement), name => ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        // The name of the host and of the user are not in it (unless a description of the system itself says the same word: a name that is a word of the system is no leak).
        var described = RuntimeInformation.OSDescription + RuntimeInformation.FrameworkDescription;
        foreach (var name in new[] { Environment.MachineName, Environment.UserName })
        {
            if (name.Length >= 5 && !described.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain(name, json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(name, markdown, StringComparison.OrdinalIgnoreCase);
            }
        }

        // No picture: no data address, no base64 of a JPEG or a PNG, and a size of numbers.
        foreach (var text in new[] { json, markdown })
        {
            Assert.DoesNotContain("data:image", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/9j/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("iVBOR", text, StringComparison.Ordinal);
            Assert.DoesNotContain(AppContext.BaseDirectory, text, StringComparison.OrdinalIgnoreCase);
            Assert.True(text.Length < 20_000);
        }
    }

    /// <summary>
    /// The probe reuses the pipeline of the qualification instead of a second registration of the services (AC4): every run takes a <see cref="QualificationPipeline"/>, and the one thing the
    /// probe adds to the registrations of that pipeline is the detector it feeds the pipeline's models with.
    /// </summary>
    [Fact]
    public void IdentityCapacityProbe_UsesThePipelineOfTheQualification_AndRegistersOnlyItsDetector()
    {
        var runs = typeof(IdentityCapacityProbe).GetMethods().Where(method => method.Name == nameof(IdentityCapacityProbe.RunAsync)).ToList();
        Assert.Contains(runs, method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(QualificationPipeline)));
        Assert.Equal(typeof(QualificationPipeline), typeof(IdentityCapacityProbe).GetMethod(nameof(IdentityCapacityProbe.CreatePipelineAsync))!.ReturnType.GetGenericArguments()[0].GetGenericArguments()[0]);

        var plain = QualificationPipeline.Describe(new QualificationPipelineOptions { IncludeEnvironmentVariables = false });
        var probe = QualificationPipeline.Describe(new QualificationPipelineOptions { IncludeEnvironmentVariables = false, ConfigureServices = services => IdentityCapacityProbe.RegisterProbeDetector(services) });

        Assert.Equal(plain.Count + 1, probe.Count);
        Assert.Equal(typeof(IFaceDetector), probe[^1].ServiceType);
        Assert.Equal(plain.Select(descriptor => descriptor.ServiceType), probe.Take(plain.Count).Select(descriptor => descriptor.ServiceType));
    }

    [Fact]
    public void CapacityFact_SkipsUnlessTheProbeIsAskedForAndTheOutputFolderIsSet()
    {
        string? Read(string name, string? probe, string? output) => name == CapacityEnvironment.ProbeVariable ? probe : name == CapacityEnvironment.OutVariable ? output : null;

        Assert.NotNull(CapacityEnvironment.SkipReason(name => Read(name, null, "/out")));
        Assert.NotNull(CapacityEnvironment.SkipReason(name => Read(name, "0", "/out")));
        Assert.NotNull(CapacityEnvironment.SkipReason(name => Read(name, "1", null)));
        Assert.NotNull(CapacityEnvironment.SkipReason(name => Read(name, "1", " ")));
        Assert.Null(CapacityEnvironment.SkipReason(name => Read(name, "1", "/out")));
        Assert.Null(CapacityEnvironment.SkipReason(name => Read(name, "TRUE", "/out")));
        Assert.Equal(CapacityEnvironment.DefaultIterations, CapacityEnvironment.Iterations(_ => null));
        Assert.Equal(CapacityEnvironment.DefaultIterations, CapacityEnvironment.Iterations(_ => "0"));
        Assert.Equal(7, CapacityEnvironment.Iterations(_ => "7"));
    }

    // ───── a slow pipeline never approves ─────

    /// <summary>
    /// Proof (security, NF-194, ADR-013-R17, decision D8): when the pipeline does not finish within <c>IdentityVerification:Presence:SequenceTimeoutMs</c> (the least the section allows, 500 ms, with a
    /// detector that never answers: a machine too slow for the guests it has), the closure answers a review with <c>technical_error</c> and the reason <c>processing: timeout</c>, in a segment qualified
    /// for the automatic approval with the global flag on: a slow CPU sends the guest to a person, it never approves anybody, and the way to a person stays open.
    /// </summary>
    [Fact]
    public async Task IdentityCapacityProbe_TimeoutBudget_GoesToReview()
    {
        await using var world = await RealPipelineWorldAsync();
        var session = await world.ReadyStayAsync();
        var issue = await world.IssueAsync(session);
        world.Limits.Presence.SequenceTimeoutMs = 500;
        PresenceStepResult answer;
        fixture.Detector.Stalls = true;
        try
        {
            answer = await world.Flow().SubmitPresenceSequenceAsync(
                session,
                PresenceClosureKit.Submission(SyntheticIdentityFixtures.NewKey(), issue.ChallengeToken),
                Ct);
        }
        finally
        {
            fixture.Detector.Stalls = false;
        }

        Assert.Equal(PresenceStepStatus.Done, answer.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        await world.AssertNeverApprovedAsync(session, answer);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate, IdentityDecisionMode.Automatic), (row.Outcome, row.ShadowOutcome, row.DecisionMode));
        Assert.Contains(VerificationReasons.Parse(row.ReasonsJson), reason => reason.Check == VerificationReasons.Checks.Processing && reason.Result == VerificationReasons.Results.Timeout);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityOutcomeCodes.TechnicalError, saved.IdentityOutcomeCode);
        Assert.Equal([(session.Id, VerificationAlertReasons.TechnicalError)], world.Inner.Alerts.Raised);

        // The guest can still ask for a person: no error, the way open, the closure as it was.
        var afterwards = await world.Flow().RequestOperatorAsync(session, Ct);
        Assert.True(afterwards.OperatorPathOpen);
        Assert.Equal(IdentityOutcomeCodes.TechnicalError, (await world.ReadBackAsync(session.Id)).IdentityOutcomeCode);
    }

    /// <summary>
    /// Proof (security, decision D8): a budget the section does not allow (1 ms is below its least) is not a budget the pipeline runs with: the section is not available, the pipeline is not asked,
    /// and a person decides, with the outcome <c>unavailable</c>, never an approval. (The brief names <c>technical_error</c> for 1 ms; the section refuses 1 ms before the pipeline runs, so the
    /// outcome is <c>unavailable</c>, and a budget that is valid and exceeded is the proof above.)
    /// </summary>
    [Fact]
    public async Task IdentityCapacityProbe_ABudgetTheSectionDoesNotAllow_GoesToReview_AsUnavailable()
    {
        await using var world = await RealPipelineWorldAsync();
        var session = await world.ReadyStayAsync();
        var issue = await world.IssueAsync(session);
        world.Limits.Presence.SequenceTimeoutMs = 1;
        var answer = await world.Flow().SubmitPresenceSequenceAsync(
            session,
            PresenceClosureKit.Submission(SyntheticIdentityFixtures.NewKey(), issue.ChallengeToken),
            Ct);

        Assert.Equal(PresenceStepStatus.Done, answer.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        await world.AssertNeverApprovedAsync(session, answer);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.Outcome, row.ShadowOutcome));
        Assert.Contains(VerificationReasons.Parse(row.ReasonsJson), reason => reason.Check == VerificationReasons.Checks.Processing && reason.Result == VerificationReasons.Results.Unavailable);
        Assert.Equal(IdentityOutcomeCodes.Unavailable, (await world.ReadBackAsync(session.Id)).IdentityOutcomeCode);
    }

    // ───── the real run ─────

    /// <summary>
    /// The real run (opt-in: <c>CASAZEN_CAPACITY_PROBE=1</c> and <c>CASAZEN_CAPACITY_OUT</c>; <c>CASAZEN_CAPACITY_ITERATIONS</c> sets the requests of each level): the probe at one, two and four requests
    /// at once over the models and the configuration of the machine it runs on (the variables of the environment count, as on Railway), the report written as <c>capacity-report.json</c> and
    /// <c>capacity-report.md</c>. Run it on the machine the check-in will run on, at the volume expected at the peak; it prints nothing but where it wrote.
    /// </summary>
    [CapacityFact]
    public async Task IdentityCapacityProbe_RealRun_WritesTheReport()
    {
        var output = Environment.GetEnvironmentVariable(CapacityEnvironment.OutVariable)!.Trim();
        fixture.Pipeline.EnsureOutputAllowed(output);

        var report = await IdentityCapacityProbe.RunAsync(
            fixture.Pipeline,
            new CapacityProbeOptions { Iterations = CapacityEnvironment.Iterations(CapacityEnvironment.Read) },
            Ct);

        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, CapacityReport.JsonFileName), report.ToJson(), Ct);
        await File.WriteAllTextAsync(Path.Combine(output, CapacityReport.MarkdownFileName), report.ToMarkdown(), Ct);
        Assert.Equal([1, 2, 4], report.Levels.Select(level => level.Concurrency));
    }

    // ───── helpers ─────

    private static readonly string[] ForbiddenNames = ["host", "hostName", "machineName", "user", "userName", "path", "folder", "directory", "computer"];

    /// <summary>A world of the closure whose pipeline is the real one of the qualification (its models and its policy) and whose segment allows the automatic approval.</summary>
    private async Task<SyntheticIdentityWorld> RealPipelineWorldAsync()
    {
        var world = await SyntheticIdentityWorld.CreateAsync(
            collector: _ => new PresenceEvidenceCollector(
                fixture.Pipeline.Validator,
                fixture.Pipeline.Selector,
                fixture.Pipeline.Liveness,
                fixture.Pipeline.Matcher,
                Options.Create(fixture.Pipeline.BiometricOptions)));
        world.Inner.Segment = EvidenceBundles.Allowed;
        return world;
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var inner in PropertyNames(property.Value))
                        yield return inner;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in PropertyNames(item))
                        yield return inner;
                }

                break;
        }
    }
}
