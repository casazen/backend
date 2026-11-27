using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Casazen.Core.Identity;
using Casazen.Core.Identity.Statistics;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Qualification;

// E74/F02/US01/T03 (NF-198, D12): the latency and capacity probe of the presence pipeline. The specification asks to measure the p95 latency, the saturation of the CPU and the memory at
// the expected peak, and the capacity of the CPU on the real volume, before buying a GPU. The inference runs inside the web process (IntraOpThreads 2) next to the API and Hangfire, so the
// probe runs the REAL pipeline of the application, the one the qualification builds (QualificationPipeline: the services the application registers, with the models of the machine, the
// published configuration), and nothing else: it registers no service of its own. Everything it feeds the pipeline is GENERATED (no photograph of a person and no picture of a document is in
// the repository, which is public) and nothing it measures or writes is more than a number, a code or the description of the machine.

/// <summary>
/// The environment variables of the opt-in run of the capacity probe (they belong to test runs only: not a setting of the application, and CI sets none). <see cref="ProbeVariable"/> set to
/// <c>1</c> asks for the run, <see cref="OutVariable"/> is the folder <c>capacity-report.json</c> and <c>capacity-report.md</c> are written to, <see cref="IterationsVariable"/>
/// (optional) the requests of each level of concurrency.
/// </summary>
public static class CapacityEnvironment
{
    public const string ProbeVariable = "CASAZEN_CAPACITY_PROBE";
    public const string OutVariable = "CASAZEN_CAPACITY_OUT";
    public const string IterationsVariable = "CASAZEN_CAPACITY_ITERATIONS";

    /// <summary>The requests of each level of concurrency of a real run when <see cref="IterationsVariable"/> says nothing.</summary>
    public const int DefaultIterations = 20;

    /// <summary>Why the run must be skipped, or null when it can run. <paramref name="read"/> reads a variable (null or blank when it is not set).</summary>
    public static string? SkipReason(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        const string OptIn = "The capacity probe is opt-in: it takes minutes of the CPU of the machine it measures, so CI never runs it.";
        if (!IsOn(read(ProbeVariable)))
            return $"{OptIn} Set {ProbeVariable}=1 and {OutVariable} (the folder the report is written to) to run it.";

        return string.IsNullOrWhiteSpace(read(OutVariable)) ? $"{OptIn} Set {OutVariable} (the folder the report is written to) to run it." : null;
    }

    /// <summary>True when the run was asked for: <c>1</c> or <c>true</c>, whatever the letter case.</summary>
    public static bool IsOn(string? value)
    {
        var text = value?.Trim();
        return string.Equals(text, "1", StringComparison.Ordinal) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The requests of each level: the variable when it is a positive integer, otherwise <see cref="DefaultIterations"/>.</summary>
    public static int Iterations(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return int.TryParse(read(IterationsVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : DefaultIterations;
    }

    /// <summary>The variable of the process; null when it is not set.</summary>
    internal static string? Read(string variable) => Environment.GetEnvironmentVariable(variable);
}

/// <summary>
/// A fact of the real run of the capacity probe: reported as skipped, with the reason, unless <c>CASAZEN_CAPACITY_PROBE=1</c> and <c>CASAZEN_CAPACITY_OUT</c> are set
/// (see <see cref="CapacityEnvironment"/>). CI sets none of them, so the long run never runs there and is never reported as passed either.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CapacityFactAttribute : FactAttribute
{
    public CapacityFactAttribute()
    {
        if (CapacityEnvironment.SkipReason(CapacityEnvironment.Read) is { } reason)
            Skip = reason;
    }
}

/// <summary>How a run of the probe goes. The defaults are the ones of a real run.</summary>
public sealed record CapacityProbeOptions
{
    /// <summary>The requests of each level of concurrency, after the warm-up (a level runs at least as many requests as it has in flight at once).</summary>
    public int Iterations { get; init; } = CapacityEnvironment.DefaultIterations;

    /// <summary>The numbers of requests in flight at once, one level each: the expected peak of a property is a few guests at the same moment.</summary>
    public IReadOnlyList<int> Concurrency { get; init; } = [1, 2, 4];

    /// <summary>The requests run before the first level and not counted (the models and the code warm up once).</summary>
    public int WarmupIterations { get; init; } = 1;

    /// <summary>The longest side of a frame; the limit of the application (<c>IdentityVerification:Presence:MaxFrameSide</c>) when null.</summary>
    public int? FrameSide { get; init; }
}

/// <summary>The machine a probe ran on: the number of processors, the system, the runtime and the threads of each model. No name of the host, of the user or of a folder.</summary>
public sealed record CapacityMachine(int ProcessorCount, string OperatingSystem, string Framework, string Architecture, int ModelThreads);

/// <summary>What the probe fed the pipeline: the size of what one request carries and the limit of time the requests are measured against.</summary>
/// <param name="Frames">The frames of the sequence: the target rate of the policy times the duration of its challenge.</param>
/// <param name="FrameWidth">The width of a frame, in pixels.</param>
/// <param name="FrameHeight">The height of a frame, in pixels.</param>
/// <param name="FrameBytes">The size of the largest JPEG of the sequence.</param>
/// <param name="DocumentPixels">The pixels of the picture of the document the portrait is cut from.</param>
/// <param name="SequenceTimeoutMs">The time the application gives the pipeline of one request (<c>IdentityVerification:Presence:SequenceTimeoutMs</c>): past it a person decides.</param>
public sealed record CapacityScene(int Frames, int FrameWidth, int FrameHeight, int FrameBytes, int DocumentPixels, int SequenceTimeoutMs);

/// <summary>One level of concurrency: what a request cost on this machine with this many in flight.</summary>
/// <param name="Concurrency">How many requests were in flight at once.</param>
/// <param name="Requests">How many requests the level ran.</param>
/// <param name="WallSeconds">How long the level took, from the first request to the last.</param>
/// <param name="RequestsPerSecond">The requests the machine finished a second at this level.</param>
/// <param name="Stages">The p50, the p95 and the largest time of each stage, in milliseconds (a stage that was not run is not listed).</param>
/// <param name="Total">The same for a whole request, the intake of the frames included.</param>
/// <param name="CpuMsPerRequest">The CPU time of the process a request took (the delta of <c>Process.TotalProcessorTime</c> over the requests), in milliseconds.</param>
/// <param name="PeakWorkingSetMb">The most memory the process held while the level ran, sampled, in megabytes.</param>
/// <param name="AllocatedMbPerRequest">The managed memory allocated for a request, in megabytes (the delta of the bytes the GC allocated over the requests).</param>
/// <param name="OverTheLimit">How many requests took longer than <see cref="CapacityScene.SequenceTimeoutMs"/> from the validation of the sequence to the match (past it the application sends the guest to a person).</param>
public sealed record CapacityLevel(
    int Concurrency,
    int Requests,
    double WallSeconds,
    double RequestsPerSecond,
    IReadOnlyList<QualificationLatency> Stages,
    QualificationLatency Total,
    double CpuMsPerRequest,
    double PeakWorkingSetMb,
    double AllocatedMbPerRequest,
    int OverTheLimit);

/// <summary>The report of a run of the probe: numbers, codes and the description of the machine, nothing else.</summary>
public sealed record CapacityReport(string Schema, CapacityMachine Machine, CapacityScene Scene, IReadOnlyList<string> Notes, IReadOnlyList<CapacityLevel> Levels)
{
    /// <summary>The name of the schema of the file, to read it back.</summary>
    public const string SchemaName = "casazen.identity-capacity.v1";

    public const string JsonFileName = "capacity-report.json";
    public const string MarkdownFileName = "capacity-report.md";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The report for a person: the machine, the scene, a table of the stages for each level and the notes.</summary>
    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.AppendLine("# Presence pipeline: latency and capacity").AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Processors: {Machine.ProcessorCount}; threads of each model: {Machine.ModelThreads}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- System: {Machine.OperatingSystem} ({Machine.Architecture}); runtime: {Machine.Framework}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- A request: {Scene.Frames} frames of {Scene.FrameWidth} x {Scene.FrameHeight} (the largest {Scene.FrameBytes} bytes) and a document of {Scene.DocumentPixels} pixels");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Limit of the application for the pipeline of a request: {Scene.SequenceTimeoutMs} ms").AppendLine();
        foreach (var level in Levels)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"## {level.Concurrency} at once");
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{level.Requests} requests in {F(level.WallSeconds)} s ({F(level.RequestsPerSecond)} a second). CPU {F(level.CpuMsPerRequest)} ms and {F(level.AllocatedMbPerRequest)} MB allocated for a request, peak memory {F(level.PeakWorkingSetMb)} MB, {level.OverTheLimit} over the limit.");
            text.AppendLine();
            text.AppendLine("| Stage | Requests | p50 ms | p95 ms | Largest ms |");
            text.AppendLine("|---|---|---|---|---|");
            foreach (var stage in level.Stages.Append(level.Total))
                text.AppendLine(CultureInfo.InvariantCulture, $"| {stage.Stage} | {stage.Count} | {F(stage.P50Ms)} | {F(stage.P95Ms)} | {F(stage.MaxMs)} |");

            text.AppendLine();
        }

        text.AppendLine("## Notes").AppendLine();
        foreach (var note in Notes)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {note}");

        return text.ToString();
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// The detector the probe gives the pipeline. The pictures of the probe are generated, so the real detector finds no face in them and the models after it would never run; this one runs
/// the REAL detector on every picture (the cost of a detection is the picture's, not the verdict's) and answers one face in the middle of the picture whatever the real one found, so that the
/// liveness and the recognition models run on every frame the way they do on a guest. With <see cref="Stalls"/> on it waits for its token to be cancelled and never answers: a machine too slow
/// to finish within the time of the request.
/// </summary>
public sealed class ProbeFaceDetector(IFaceDetector real) : IFaceDetector
{
    /// <summary>The detector never answers: it waits (up to 20 seconds) for the token to be cancelled, then raises the cancellation.</summary>
    public bool Stalls { get; set; }

    public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
    {
        if (Stalls)
        {
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            cancellationToken.ThrowIfCancellationRequested();
        }

        _ = real.Detect(image, minScore, cancellationToken);
        return [SequenceTestFrames.Face(image.Width, image.Height)];
    }
}

/// <summary>
/// The probe of the latency and the capacity of the presence pipeline (E74/F02/US01/T03, NF-198). <see cref="RunAsync(QualificationPipeline, CapacityProbeOptions, CancellationToken)"/> runs, for
/// each level of concurrency, the requests the application would run for a guest, over the <see cref="QualificationPipeline"/> it is given (it registers no service of its own): the intake of the
/// frames with the limits of the application, the validation of the sequence, the choice of the frames by the server, the liveness of the chosen frames and the comparison of the portrait of the
/// document with them. It measures, per stage and per request, the p50, the p95 and the largest time (<see cref="IdentityStatistics.Percentile"/>), and per level the CPU time, the peak working set
/// and the managed memory a request took, and how many requests took longer than <c>IdentityVerification:Presence:SequenceTimeoutMs</c>.
/// <para>
/// <b>What it measures.</b> The pictures are generated: frames at the longest side the application accepts, as many as the target rate of the policy gives for the duration of its challenge, and a
/// picture of a document. The stages run one after the other whatever the verdict of the one before (a guest who is live and matches is the request whose cost is measured), on the frames the server
/// would choose, or on frames spread over the sequence when the validation of the generated pictures does not pass. The verdicts are not measured: the qualification measures them. Every limit of time of the
/// pipeline is the widened one of the qualification, so a slow machine finishes and is counted over the limit instead of being stopped.
/// </para>
/// <para><b>Privacy.</b> Nothing is stored, logged or printed but numbers; the frames are zeroed when each request ends.</para>
/// </summary>
public static class IdentityCapacityProbe
{
    /// <summary>The names of the stages of a request, those of <see cref="QualificationStages"/>.</summary>
    private static readonly string[] StageNames =
    [
        QualificationStages.PresenceIntake,
        QualificationStages.PresenceValidation,
        QualificationStages.PresenceSelection,
        QualificationStages.PresenceLiveness,
        QualificationStages.PresenceMatch,
    ];

    /// <summary>The name of the total of a request.</summary>
    public const string TotalStage = "total";

    /// <summary>
    /// The whole probe: builds the pipeline of the qualification (with the detector of the probe, <see cref="ProbeFaceDetector"/>), waits for the models, runs
    /// <see cref="RunAsync(QualificationPipeline, CapacityProbeOptions, CancellationToken)"/> and releases the pipeline.
    /// </summary>
    public static async Task<CapacityReport> RunAsync(CapacityProbeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (pipeline, _) = await CreatePipelineAsync(includeEnvironmentVariables: true, ct);
        await using (pipeline)
            return await RunAsync(pipeline, options, ct);
    }

    /// <summary>
    /// The pipeline of the qualification with the detector of the probe in place of the one of the models alone. It is the one pipeline the application registers (the same services, the
    /// published configuration, the models of the machine): only the answer of the detector, which no generated picture can make a face, is the probe's.
    /// </summary>
    public static async Task<(QualificationPipeline Pipeline, ProbeFaceDetector Detector)> CreatePipelineAsync(bool includeEnvironmentVariables, CancellationToken ct = default)
    {
        ProbeFaceDetector? detector = null;
        var pipeline = await QualificationPipeline.CreateAsync(
            new QualificationPipelineOptions
            {
                IncludeEnvironmentVariables = includeEnvironmentVariables,
                ConfigureServices = services => RegisterProbeDetector(services, created => detector = created),
            },
            ct);
        return (pipeline, detector ?? throw new InvalidOperationException("The pipeline did not ask for the face detector."));
    }

    /// <summary>
    /// The one registration the probe makes over the registrations of the pipeline of the qualification: the face detector of the models, wrapped in the detector of the probe
    /// (<see cref="ProbeFaceDetector"/>). <paramref name="created"/> is told the detector when the container builds it.
    /// </summary>
    public static void RegisterProbeDetector(IServiceCollection services, Action<ProbeFaceDetector>? created = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IFaceDetector>(provider =>
        {
            var detector = new ProbeFaceDetector(ActivatorUtilities.CreateInstance<FaceDetector>(provider));
            created?.Invoke(detector);
            return detector;
        });
    }

    /// <summary>The levels of <paramref name="options"/> over <paramref name="pipeline"/>.</summary>
    public static async Task<CapacityReport> RunAsync(QualificationPipeline pipeline, CapacityProbeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Iterations < 1 || options.WarmupIterations < 0 || options.Concurrency.Count == 0 || options.Concurrency.Any(level => level < 1))
            throw new ArgumentException("The probe needs at least one request at each level of concurrency.", nameof(options));

        var policy = pipeline.ActivePolicy;
        var limit = LiveLimitMs(pipeline);
        using var scene = ProbeScene.Build(pipeline, policy, options.FrameSide ?? pipeline.IdentityOptions.Presence.MaxFrameSide);

        for (var warm = 0; warm < options.WarmupIterations; warm++)
            await RunRequestAsync(pipeline, scene, policy, ct);

        var levels = new List<CapacityLevel>(options.Concurrency.Count);
        foreach (var concurrency in options.Concurrency)
            levels.Add(await RunLevelAsync(pipeline, scene, policy, concurrency, Math.Max(options.Iterations, concurrency), limit, ct));

        return new CapacityReport(
            CapacityReport.SchemaName,
            new CapacityMachine(
                Environment.ProcessorCount,
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                pipeline.BiometricOptions.IntraOpThreads),
            new CapacityScene(scene.Jpegs.Count, scene.Width, scene.Height, scene.Jpegs.Max(jpeg => jpeg.Length), scene.DocumentPixels, limit),
            [
                "The pictures are generated: no photograph of a person and no picture of a document was used, and none is stored.",
                "The detector of the models runs on every picture; a fixed face stands for its answer, so that the liveness and the recognition models run on every frame as they do on a guest.",
                "The stages run whatever the verdict of the one before: the verdicts are measured by the qualification, not here.",
                "The time limits of the pipeline are the widened ones of the qualification: a slow request finishes and is counted over the limit of the application.",
                "A stage that was not run (the choice of the frames, when the generated pictures do not pass the validation) is not listed.",
            ],
            levels);
    }

    /// <summary>One level: <paramref name="requests"/> requests, <paramref name="concurrency"/> at a time, with the CPU, the memory and the allocations of the process measured around them.</summary>
    private static async Task<CapacityLevel> RunLevelAsync(
        QualificationPipeline pipeline,
        ProbeScene scene,
        IdentityDecisionPolicy policy,
        int concurrency,
        int requests,
        int limitMs,
        CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var results = new ConcurrentBag<RequestTimings>();
        var next = 0;
        var peakBytes = 0L;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(
            async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    process.Refresh();
                    Interlocked.Exchange(ref peakBytes, Math.Max(Interlocked.Read(ref peakBytes), process.WorkingSet64));
                    try
                    {
                        await Task.Delay(20, sampling.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            CancellationToken.None);

        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, concurrency).Select(_ => Task.Run(
            async () =>
            {
                while (Interlocked.Increment(ref next) <= requests)
                    results.Add(await RunRequestAsync(pipeline, scene, policy, ct));
            },
            ct)).ToList();
        await Task.WhenAll(workers);
        var wall = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        await sampling.CancelAsync();
        await sampler;
        process.Refresh();
        var peak = Math.Max(Interlocked.Read(ref peakBytes), process.WorkingSet64);

        var done = results.ToList();
        var stages = new List<QualificationLatency>();
        foreach (var stage in StageNames)
        {
            var values = done.Where(timings => timings.Stages.ContainsKey(stage)).Select(timings => timings.Stages[stage]).ToList();
            if (values.Count > 0)
                stages.Add(Latency(stage, values));
        }

        var totals = done.Select(timings => timings.TotalMs).ToList();
        var overTheLimit = done.Count(timings => timings.PipelineMs > limitMs);
        const double Megabyte = 1024.0 * 1024.0;
        return new CapacityLevel(
            concurrency,
            done.Count,
            Round(wall.TotalSeconds),
            Round(done.Count / Math.Max(wall.TotalSeconds, 1e-9)),
            stages,
            Latency(TotalStage, totals),
            Round(cpu.TotalMilliseconds / done.Count),
            Round(peak / Megabyte),
            Round(allocated / Megabyte / done.Count),
            overTheLimit);
    }

    /// <summary>What one request did: the time of each stage it ran, the pipeline from the validation to the match, and the whole of it.</summary>
    private sealed record RequestTimings(IReadOnlyDictionary<string, double> Stages, double PipelineMs, double TotalMs);

    /// <summary>
    /// One request, as the closure of the presence runs it: the frames read with the limits of the application, then the validation, the choice of the frames, the liveness and the match.
    /// Every picture it makes is zeroed before it returns.
    /// </summary>
    private static async Task<RequestTimings> RunRequestAsync(QualificationPipeline pipeline, ProbeScene scene, IdentityDecisionPolicy policy, CancellationToken ct)
    {
        var stages = new Dictionary<string, double>(StringComparer.Ordinal);
        var request = Stopwatch.GetTimestamp();

        var started = Stopwatch.GetTimestamp();
        var parts = scene.Jpegs.Select(jpeg => new FramePart(new MemoryStream(jpeg, writable: false), jpeg.Length, "image/jpeg")).ToList();
        var read = await pipeline.FrameReader.ReadAsync(parts, scene.TimestampsJson, pipeline.Clock.GetUtcNow(), ct);
        stages[QualificationStages.PresenceIntake] = Since(started);
        if (!read.Succeeded)
            throw new InvalidOperationException($"The sequence of the probe was refused by the intake of the application ({read.ErrorCode}).");

        using var sequence = read.Sequence;
        var pipelineStarted = Stopwatch.GetTimestamp();
        started = pipelineStarted;
        var validation = await pipeline.Validator.ValidateAsync(sequence, scene.Challenge, policy, ct);
        stages[QualificationStages.PresenceValidation] = Since(started);

        // The frames the server would choose when the validation passes; else the frames spread over the sequence: the cost of the models does not depend on which frames they are given.
        IReadOnlyList<int> livenessFrames = Spread(sequence.Frames.Count, policy.Presence.Liveness.Frames);
        IReadOnlyList<int> matchFrames = Spread(sequence.Frames.Count, policy.Match.Frames);
        if (validation.Passed)
        {
            started = Stopwatch.GetTimestamp();
            var selection = pipeline.Selector.Select(sequence, validation, policy);
            stages[QualificationStages.PresenceSelection] = Since(started);
            if (selection.IsSelected)
            {
                livenessFrames = selection.Frames.LivenessIndices;
                matchFrames = selection.Frames.MatchIndices;
            }
        }

        started = Stopwatch.GetTimestamp();
        _ = await pipeline.Liveness.EvaluateAsync(sequence, livenessFrames, policy, ct);
        stages[QualificationStages.PresenceLiveness] = Since(started);

        // The picture of the document is decoded for every request, as the closure does: the kept picture is encrypted and read again for each attempt.
        started = Stopwatch.GetTimestamp();
        using (var document = RasterImage.Decode(scene.DocumentFront, pipeline.BiometricOptions.MaxImagePixels))
            _ = await pipeline.Matcher.MatchAsync(document, sequence, matchFrames, policy, ct);

        stages[QualificationStages.PresenceMatch] = Since(started);
        return new RequestTimings(stages, Since(pipelineStarted), Since(request));
    }

    /// <summary>The time the application gives the pipeline of one request: the section of the published configuration, which the pipeline widens for itself.</summary>
    private static int LiveLimitMs(QualificationPipeline pipeline)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false, reloadOnChange: false).Build();
        var published = configuration.GetSection(IdentityVerificationOptions.SectionName).Get<IdentityVerificationOptions>() ?? new IdentityVerificationOptions();
        _ = pipeline;
        return published.Presence.SequenceTimeoutMs;
    }

    /// <summary><paramref name="count"/> places spread evenly over <paramref name="frames"/> frames, in order, never the same twice.</summary>
    private static List<int> Spread(int frames, int count)
    {
        var places = new List<int>(count);
        for (var index = 0; index < count; index++)
        {
            var place = count == 1 ? frames / 2 : (int)Math.Round(index * (frames - 1.0) / (count - 1));
            if (places.Count == 0 || place > places[^1])
                places.Add(place);
        }

        return places;
    }

    private static QualificationLatency Latency(string stage, IReadOnlyList<double> values) =>
        new(stage, values.Count, Round(IdentityStatistics.Percentile(values, 50)!.Value), Round(IdentityStatistics.Percentile(values, 95)!.Value), Round(values.Max()));

    private static double Since(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static double Round(double value) => Math.Round(value, 3);

    /// <summary>
    /// What a request is made of, generated once for a run: the JPEG of each frame, their timestamps, the challenge they answer and the picture of the document. Frames are the size and the
    /// number of the policy; their pixels are blocky texture (a few pixels a cell) plus a slope, so the JPEG weighs what a camera frame weighs and not what noise would.
    /// </summary>
    private sealed class ProbeScene : IDisposable
    {
        private ProbeScene(IReadOnlyList<byte[]> jpegs, string timestampsJson, int width, int height, StoredPresenceChallenge challenge, byte[] documentFront, int documentPixels)
        {
            Jpegs = jpegs;
            TimestampsJson = timestampsJson;
            Width = width;
            Height = height;
            Challenge = challenge;
            DocumentFront = documentFront;
            DocumentPixels = documentPixels;
        }

        public IReadOnlyList<byte[]> Jpegs { get; }

        public string TimestampsJson { get; }

        public int Width { get; }

        public int Height { get; }

        public StoredPresenceChallenge Challenge { get; }

        public byte[] DocumentFront { get; }

        public int DocumentPixels { get; }

        public static ProbeScene Build(QualificationPipeline pipeline, IdentityDecisionPolicy policy, int side)
        {
            var challenge = policy.Presence.Challenge;
            var sequence = policy.Presence.Sequence;
            var interval = (long)Math.Round(1000.0 / sequence.TargetFps);
            var durationMs = challenge.LeadInMs + ((long)challenge.Steps * challenge.StepMs);
            var frames = Math.Clamp((int)(durationMs / interval) + 1, sequence.MinFrames, sequence.MaxFrames);
            var width = side;
            var height = side * 3 / 4;

            var jpegs = new List<byte[]>(frames);
            for (var index = 0; index < frames; index++)
                jpegs.Add(TestJpeg.Gray(width, height, Texture(width, height, 7000 + index)));

            var limits = pipeline.IdentityOptions.Presence;
            if (jpegs.Any(jpeg => jpeg.Length > limits.MaxFrameBytes) || jpegs.Sum(jpeg => (long)jpeg.Length) > limits.MaxSequenceBytes)
                throw new InvalidOperationException("The generated frames are larger than the intake of the application accepts: the probe would measure a refusal.");

            var steps = challenge.Actions.Take(challenge.Steps).ToList();
            var issued = pipeline.Clock.GetUtcNow();
            var stored = new StoredPresenceChallenge(Guid.NewGuid(), steps, issued, issued.AddSeconds(challenge.LifetimeSeconds), 1);
            var timestamps = PreparedSequence.TimestampsOf(Enumerable.Range(0, frames).Select(index => index * interval));

            // A card on a table, as large as a photograph of a phone of the page (1280 x 960): drawn in code.
            var format = pipeline.Formats.Get("it-cie-3.0") ?? throw new InvalidOperationException("The Italian card is not in the registry.");
            using var card = DocumentImageFactory.Render(format, DocumentSide.Front, new DocumentImageFactory.Placement(160, 140, 960, 606), imageWidth: 1280, imageHeight: 960);
            return new ProbeScene(jpegs, timestamps, width, height, stored, CardPngEncoder.Encode(card), card.Width * card.Height);
        }

        public void Dispose()
        {
            foreach (var jpeg in Jpegs)
                Array.Clear(jpeg);

            Array.Clear(DocumentFront);
        }

        /// <summary>Gray pixels: cells of six by six pixels at random levels (six either way) around the middle, a slope across the picture, the same for the same seed; a JPEG of it weighs what a camera frame weighs.</summary>
        private static byte[] Texture(int width, int height, int seed)
        {
            const int Cell = 6;
            var random = new Random(seed);
            var cellsAcross = (width + Cell - 1) / Cell;
            var cellsDown = (height + Cell - 1) / Cell;
            var cells = new byte[cellsAcross * cellsDown];
            for (var cell = 0; cell < cells.Length; cell++)
                cells[cell] = (byte)random.Next(122, 135);

            var pixels = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var slope = (x * 16 / width) - 8;
                    pixels[(y * width) + x] = (byte)Math.Clamp(cells[((y / Cell) * cellsAcross) + (x / Cell)] + slope, 0, 255);
                }
            }

            return pixels;
        }
    }
}
