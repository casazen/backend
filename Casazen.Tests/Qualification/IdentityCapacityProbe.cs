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

// Variables of test runs only, not settings of the application: CI sets none.
public static class CapacityEnvironment
{
    public const string ProbeVariable = "CASAZEN_CAPACITY_PROBE";
    public const string OutVariable = "CASAZEN_CAPACITY_OUT";
    public const string IterationsVariable = "CASAZEN_CAPACITY_ITERATIONS";

    public const int DefaultIterations = 20;

    public static string? SkipReason(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        const string OptIn = "The capacity probe is opt-in: it takes minutes of the CPU of the machine it measures, so CI never runs it.";
        if (!IsOn(read(ProbeVariable)))
            return $"{OptIn} Set {ProbeVariable}=1 and {OutVariable} (the folder the report is written to) to run it.";

        return string.IsNullOrWhiteSpace(read(OutVariable)) ? $"{OptIn} Set {OutVariable} (the folder the report is written to) to run it." : null;
    }

    public static bool IsOn(string? value)
    {
        var text = value?.Trim();
        return string.Equals(text, "1", StringComparison.Ordinal) || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static int Iterations(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return int.TryParse(read(IterationsVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : DefaultIterations;
    }

    internal static string? Read(string variable) => Environment.GetEnvironmentVariable(variable);
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CapacityFactAttribute : FactAttribute
{
    public CapacityFactAttribute()
    {
        if (CapacityEnvironment.SkipReason(CapacityEnvironment.Read) is { } reason)
            Skip = reason;
    }
}

public sealed record CapacityProbeOptions
{
    public int Iterations { get; init; } = CapacityEnvironment.DefaultIterations;

    public IReadOnlyList<int> Concurrency { get; init; } = [1, 2, 4];

    public int WarmupIterations { get; init; } = 1;

    public int? FrameSide { get; init; }
}

public sealed record CapacityMachine(int ProcessorCount, string OperatingSystem, string Framework, string Architecture, int ModelThreads);

public sealed record CapacityScene(int Frames, int FrameWidth, int FrameHeight, int FrameBytes, int DocumentPixels, int SequenceTimeoutMs);

// OverTheLimit: requests that took longer than the application allows from the validation to the match.
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

public sealed record CapacityReport(string Schema, CapacityMachine Machine, CapacityScene Scene, IReadOnlyList<string> Notes, IReadOnlyList<CapacityLevel> Levels)
{
    public const string SchemaName = "casazen.identity-capacity.v1";

    public const string JsonFileName = "capacity-report.json";
    public const string MarkdownFileName = "capacity-report.md";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

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

// Generated pictures hold no face: the detection is run for its cost, and a fixed face lets the liveness and the recognition models run on every frame
// as they do for a guest.
public sealed class ProbeFaceDetector(IFaceDetector real) : IFaceDetector
{
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

public static class IdentityCapacityProbe
{
    private static readonly string[] StageNames =
    [
        QualificationStages.PresenceIntake,
        QualificationStages.PresenceValidation,
        QualificationStages.PresenceSelection,
        QualificationStages.PresenceLiveness,
        QualificationStages.PresenceMatch,
    ];

    public const string TotalStage = "total";

    public static async Task<CapacityReport> RunAsync(CapacityProbeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (pipeline, _) = await CreatePipelineAsync(includeEnvironmentVariables: true, ct);
        await using (pipeline)
            return await RunAsync(pipeline, options, ct);
    }

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

    private sealed record RequestTimings(IReadOnlyDictionary<string, double> Stages, double PipelineMs, double TotalMs);

    // Every stage runs whatever the verdict of the one before: the cost measured is the one of a guest who passes.
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

        // The frames the server would choose when the validation passes, else frames spread over the sequence: the cost of the models does not depend on which they are.
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

        // Decoded for every request, as the closure does: the kept picture is encrypted and read again for each attempt.
        started = Stopwatch.GetTimestamp();
        using (var document = RasterImage.Decode(scene.DocumentFront, pipeline.BiometricOptions.MaxImagePixels))
            _ = await pipeline.Matcher.MatchAsync(document, sequence, matchFrames, policy, ct);

        stages[QualificationStages.PresenceMatch] = Since(started);
        return new RequestTimings(stages, Since(pipelineStarted), Since(request));
    }

    private static int LiveLimitMs(QualificationPipeline pipeline)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: false, reloadOnChange: false).Build();
        var published = configuration.GetSection(IdentityVerificationOptions.SectionName).Get<IdentityVerificationOptions>() ?? new IdentityVerificationOptions();
        _ = pipeline;
        return published.Presence.SequenceTimeoutMs;
    }

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

            // The size of a photograph taken by the page on a phone.
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

        // Blocky texture plus a slope: a JPEG of it weighs what a camera frame weighs, not what noise would.
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
