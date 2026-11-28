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

public sealed class CapacityProbeFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CapacityReport? _small;

    public QualificationPipeline Pipeline { get; private set; } = null!;

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

        // With one request p50, p95 and the largest time are the same number.
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

        var policy = fixture.Pipeline.ActivePolicy;
        var challenge = policy.Presence.Challenge;
        var interval = (long)Math.Round(1000.0 / policy.Presence.Sequence.TargetFps);
        Assert.Equal((int)((challenge.LeadInMs + ((long)challenge.Steps * challenge.StepMs)) / interval) + 1, report.Scene.Frames);
        Assert.Equal(fixture.Pipeline.IdentityOptions.Presence.MaxFrameSide, report.Scene.FrameWidth);
        Assert.True(report.Scene.FrameBytes <= fixture.Pipeline.IdentityOptions.Presence.MaxFrameBytes);

        // The limit is the one of the application, not the widened one of the qualification.
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

    [Fact]
    public async Task IdentityCapacityProbe_Report_HoldsNoHostNameAndNoImageData()
    {
        var report = await fixture.SmallRunAsync();
        var json = report.ToJson();
        var markdown = report.ToMarkdown();

        Assert.Equal(
            ["Architecture", "Framework", "ModelThreads", "OperatingSystem", "ProcessorCount"],
            typeof(CapacityMachine).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(Environment.ProcessorCount, document.RootElement.GetProperty("machine").GetProperty("processorCount").GetInt32());
            Assert.DoesNotContain(PropertyNames(document.RootElement), name => ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        // A name that is also a word of the description of the system is no leak.
        var described = RuntimeInformation.OSDescription + RuntimeInformation.FrameworkDescription;
        foreach (var name in new[] { Environment.MachineName, Environment.UserName })
        {
            if (name.Length >= 5 && !described.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain(name, json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(name, markdown, StringComparison.OrdinalIgnoreCase);
            }
        }

        foreach (var text in new[] { json, markdown })
        {
            Assert.DoesNotContain("data:image", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/9j/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("iVBOR", text, StringComparison.Ordinal);
            Assert.DoesNotContain(AppContext.BaseDirectory, text, StringComparison.OrdinalIgnoreCase);
            Assert.True(text.Length < 20_000);
        }
    }

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

    // 500 ms is the least the section allows: the detector that never answers makes the pipeline outlast it.
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

        var afterwards = await world.Flow().RequestOperatorAsync(session, Ct);
        Assert.True(afterwards.OperatorPathOpen);
        Assert.Equal(IdentityOutcomeCodes.TechnicalError, (await world.ReadBackAsync(session.Id)).IdentityOutcomeCode);
    }

    // 1 ms is below the least the section allows: the section is unavailable, the pipeline is not asked and a person decides.
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

    private static readonly string[] ForbiddenNames = ["host", "hostName", "machineName", "user", "userName", "path", "folder", "directory", "computer"];

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
