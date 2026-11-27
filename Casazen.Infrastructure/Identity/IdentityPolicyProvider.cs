using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Identity;

/// <summary>Whether the decision policy can be used.</summary>
public enum IdentityPolicyState
{
    /// <summary>The file was read, is valid and its canonical SHA-256 is the pinned one.</summary>
    Ready,

    /// <summary>The policy cannot be used, for the reason code of the snapshot: every verification goes to a person.</summary>
    Unavailable,
}

/// <summary>The reasons a policy is <see cref="IdentityPolicyState.Unavailable"/> (codes only: the logs and the counters speak them).</summary>
public static class IdentityPolicyReasonCodes
{
    /// <summary>There is no file at <c>IdentityVerification:PolicyFile</c>, or it cannot be read.</summary>
    public const string PolicyMissing = "policy_missing";

    /// <summary>The file is not strict JSON, lacks a member, has one it should not, or a value is out of its bounds.</summary>
    public const string PolicyInvalid = "policy_invalid";

    /// <summary>The canonical SHA-256 of the file is not <c>IdentityVerification:PolicySha256</c>: the file was changed after it was pinned.</summary>
    public const string PolicyHashMismatch = "policy_hash_mismatch";

    /// <summary><c>IdentityVerification:PolicySha256</c> is empty: a policy that is not pinned is never used.</summary>
    public const string PolicyHashNotConfigured = "policy_hash_not_configured";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [PolicyMissing, PolicyInvalid, PolicyHashMismatch, PolicyHashNotConfigured];
}

/// <summary>
/// What the provider knows of the policy, read once.
/// <para>
/// <see cref="IdentityPolicyState.Ready"/>: <see cref="Policy"/> is the valid policy, <see cref="Hash"/> its canonical SHA-256 (64 lower case hexadecimal characters, the one that was
/// pinned) and <see cref="ReasonCode"/> is null. <see cref="IdentityPolicyState.Unavailable"/>: <see cref="ReasonCode"/> is a code of
/// <see cref="IdentityPolicyReasonCodes"/>, <see cref="Policy"/> and <see cref="Hash"/> are null and <see cref="Provisional"/> is true, so that a reader that
/// forgets to look at the state still never treats the policy as calibrated. A provider never throws instead of returning this.
/// </para>
/// </summary>
/// <param name="State">Whether the policy can be used.</param>
/// <param name="ReasonCode">Why it cannot, a code of <see cref="IdentityPolicyReasonCodes"/>; null when ready.</param>
/// <param name="Policy">The policy; null unless ready.</param>
/// <param name="Hash">The canonical SHA-256 of the policy file, lower case hexadecimal; null unless ready.</param>
/// <param name="Provisional">True while the policy is provisional (never qualified) and always true when unavailable.</param>
public sealed record IdentityPolicySnapshot(IdentityPolicyState State, string? ReasonCode, IdentityDecisionPolicy? Policy, string? Hash, bool Provisional)
{
    /// <summary>True when <see cref="State"/> is <see cref="IdentityPolicyState.Ready"/>: then <see cref="Policy"/> and <see cref="Hash"/> are set.</summary>
    [MemberNotNullWhen(true, nameof(Policy), nameof(Hash))]
    public bool IsReady => State == IdentityPolicyState.Ready;
}

/// <summary>
/// The versioned decision policy of the self-hosted identity verification (E70/F01/US02/T01, NF-193, NF-194, ADR-013-R18, R19): the one file that holds every
/// threshold and aggregation rule, loaded <b>once</b> from <see cref="IdentityVerificationOptions.PolicyFile"/> (relative to the application folder, or an
/// absolute path), validated, and used only when its <b>canonical</b> SHA-256 is <see cref="IdentityVerificationOptions.PolicySha256"/>.
/// <para>
/// <b>Frozen.</b> The canonical hash (see <see cref="IdentityConfigurationJson"/>: members sorted, no white space, invariant numbers, SHA-256 of the UTF-8
/// text, lower case hexadecimal) is the same for the LF and the CRLF checkout of one file, and differs as soon as a value, a name or a word changes. A changed
/// threshold therefore cannot be used silently: the file stops matching its pin and the policy is unavailable. <b>A qualified policy file is never edited.</b>
/// A new calibration is a NEW file (<c>identity-policy-YYYY.MM-N.json</c>, with its own <c>version</c>) with its own hash, pinned in
/// <c>IdentityVerification:PolicySha256</c> together with <c>IdentityVerification:PolicyFile</c>; a segment qualified for the old policy goes back to
/// observation by itself, because it was qualified for the old hash.
/// </para>
/// <para>
/// <b>Fail closed.</b> <see cref="Snapshot"/> never throws. A missing or unreadable file, an invalid member, a pin that is empty or different: each gives
/// <see cref="IdentityPolicyState.Unavailable"/> with its code (<see cref="IdentityPolicyReasonCodes"/>), and the decision engine turns it into the review
/// of a person, never into an approval. What is logged is the code, the version, the hash and the NAME of a member that is wrong, never a value of a person.
/// </para>
/// <para>
/// <b>Provisional.</b> The committed file says <c>"provisional": true</c>: a provisional policy can never be qualified for a segment, so nothing is approved
/// automatically until a calibrated policy file is committed. No decision number is written in C#: the records of <see cref="IdentityDecisionPolicy"/> have
/// no default, and the bounds checked here are the structure of a policy (a challenge of more than one bit, a lifetime of at most five minutes, ...), not a
/// calibration.
/// </para>
/// </summary>
public sealed class IdentityPolicyProvider
{
    /// <summary>
    /// The fewest bits of entropy a presence challenge must have, whatever the policy says (NF-192, AC-NF192-1): "more than one bit" means at least two, so a
    /// challenge of one move out of three, or of any number of moves out of two that never repeat in a row, is refused.
    /// </summary>
    public const double MinChallengeEntropyBits = 2.0;

    /// <summary>The longest a challenge may live, in seconds (5 minutes), whatever the policy says.</summary>
    public const int MaxChallengeLifetimeSeconds = 300;

    /// <summary>The largest policy file that is read, in bytes: a policy is a few kilobytes.</summary>
    private const int MaxFileBytes = 262_144;

    private readonly IOptions<IdentityVerificationOptions> _options;
    private readonly ILogger<IdentityPolicyProvider> _logger;
    private readonly string _baseDirectory;
    private readonly Lazy<IdentityPolicySnapshot> _snapshot;

    /// <summary>The provider of the application: the folder of the policy file is the application folder.</summary>
    public IdentityPolicyProvider(IOptions<IdentityVerificationOptions> options, ILogger<IdentityPolicyProvider> logger)
        : this(options, logger, AppContext.BaseDirectory)
    {
    }

    /// <summary>The provider with the folder a relative <c>PolicyFile</c> starts from: what the tests use.</summary>
    internal IdentityPolicyProvider(IOptions<IdentityVerificationOptions> options, ILogger<IdentityPolicyProvider> logger, string baseDirectory)
    {
        _options = options;
        _logger = logger;
        _baseDirectory = baseDirectory;
        _snapshot = new Lazy<IdentityPolicySnapshot>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The policy, read at the first use and kept for the life of the process. Never throws.</summary>
    public IdentityPolicySnapshot Snapshot => _snapshot.Value;

    /// <summary>
    /// The canonical SHA-256 (lower case hexadecimal) of the bytes of a policy file, the value to pin in <c>IdentityVerification:PolicySha256</c> for a new
    /// calibration; null when the bytes are not strict JSON. The same for every line ending, indentation and order of the members.
    /// </summary>
    public static string? CanonicalSha256(ReadOnlyMemory<byte> utf8Json)
    {
        return IdentityConfigurationJson.TryCanonicalize(utf8Json, out var canonical) ? IdentityConfigurationJson.Sha256Hex(canonical) : null;
    }

    /// <summary>
    /// The entropy, in bits, of a challenge of <paramref name="steps"/> moves drawn from <paramref name="actions"/> distinct moves:
    /// log2(actions x (actions - 1)^(steps - 1)) when a move never follows itself, log2(actions^steps) when it may. Zero when no challenge can be drawn
    /// (no move, no step, or one move that must not repeat for more than one step).
    /// </summary>
    public static double ChallengeEntropyBits(int actions, int steps, bool noImmediateRepeat)
    {
        if (actions < 1 || steps < 1)
            return 0;

        if (!noImmediateRepeat)
            return steps * Math.Log2(actions);

        // One move that must not repeat: a single step draws it with certainty (log2(1) = 0 bits), more steps cannot be drawn at all.
        if (actions == 1)
            return 0;

        return Math.Log2(actions) + ((steps - 1) * Math.Log2(actions - 1));
    }

    /// <summary>The members of a policy that are out of their bounds, by their name in the file and what is wrong with them; empty when it is valid. Never a value.</summary>
    internal static IReadOnlyList<string> Validate(IdentityDecisionPolicy policy)
    {
        var failures = new List<string>();
        CheckVersionName(failures, "version", policy.Version);
        CheckVersionName(failures, "captureRules", policy.CaptureRules);

        var document = policy.Document;
        CheckRange(failures, "document.maxRecapturesPerSide", document.MaxRecapturesPerSide, 0, 10);
        CheckRange(failures, "document.identifyingFields.count", document.IdentifyingFields.Count, 1, 10);
        CheckDistinct(failures, "document.identifyingFields", document.IdentifyingFields, IsMemberName);
        CheckRange(failures, "document.perceptualDuplicateMaxDistance", document.PerceptualDuplicateMaxDistance, 0, 64);

        var challenge = policy.Presence.Challenge;
        CheckRange(failures, "presence.challenge.steps", challenge.Steps, 1, 10);
        CheckDistinct(failures, "presence.challenge.actions", challenge.Actions, action => PresenceActions.All.Contains(action));
        CheckRange(failures, "presence.challenge.lifetimeSeconds", challenge.LifetimeSeconds, 1, MaxChallengeLifetimeSeconds);
        CheckRange(failures, "presence.challenge.stepMs", challenge.StepMs, 1, 60_000);
        CheckRange(failures, "presence.challenge.leadInMs", challenge.LeadInMs, 0, 60_000);
        var distinctActions = challenge.Actions.Where(action => action is not null).Distinct(StringComparer.Ordinal).Count();
        if (ChallengeEntropyBits(distinctActions, challenge.Steps, challenge.NoImmediateRepeat) + 1e-9 < MinChallengeEntropyBits)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"presence.challenge (actions, steps, noImmediateRepeat) must have at least {MinChallengeEntropyBits:0} bits of entropy."));
        }

        var sequence = policy.Presence.Sequence;
        CheckRange(failures, "presence.sequence.targetFps", sequence.TargetFps, 1, 60);
        CheckRange(failures, "presence.sequence.minFrames", sequence.MinFrames, 1, 600);
        CheckRange(failures, "presence.sequence.maxFrames", sequence.MaxFrames, 1, 600);
        if (sequence.MinFrames > sequence.MaxFrames)
            failures.Add("presence.sequence.minFrames must not be above presence.sequence.maxFrames.");

        CheckRange(failures, "presence.sequence.minDurationMs", sequence.MinDurationMs, 1, 120_000);
        CheckRange(failures, "presence.sequence.maxDurationMs", sequence.MaxDurationMs, 1, 120_000);
        if (sequence.MinDurationMs > sequence.MaxDurationMs)
            failures.Add("presence.sequence.minDurationMs must not be above presence.sequence.maxDurationMs.");

        CheckRange(failures, "presence.sequence.maxGapMs", sequence.MaxGapMs, 1, 60_000);
        CheckShare(failures, "presence.sequence.maxShareWithoutFace", sequence.MaxShareWithoutFace);
        CheckRange(failures, "presence.sequence.detectorMaxSide", sequence.DetectorMaxSide, 32, 4_096);

        var continuity = policy.Presence.Continuity;
        CheckThreshold(failures, "presence.continuity.maxCenterJump", continuity.MaxCenterJump);
        CheckThreshold(failures, "presence.continuity.maxSizeChange", continuity.MaxSizeChange);
        CheckThreshold(failures, "presence.continuity.minPairSimilarity", continuity.MinPairSimilarity);
        CheckRange(failures, "presence.continuity.pairSamples", continuity.PairSamples, 1, 60);

        var pose = policy.Presence.Pose;
        CheckDegrees(failures, "presence.pose.turnMinYawDegrees", pose.TurnMinYawDegrees);
        CheckDegrees(failures, "presence.pose.lookMinPitchDegrees", pose.LookMinPitchDegrees);
        CheckDegrees(failures, "presence.pose.neutralMaxDegrees", pose.NeutralMaxDegrees);

        var liveness = policy.Presence.Liveness;
        CheckRange(failures, "presence.liveness.frames", liveness.Frames, 1, 60);
        CheckThreshold(failures, "presence.liveness.minScore", liveness.MinScore);
        if (!IdentityLivenessRules.All.Contains(liveness.Rule))
            failures.Add("presence.liveness.rule must be a known rule.");

        var match = policy.Match;
        CheckRange(failures, "match.frames", match.Frames, 1, 20);
        CheckRange(failures, "match.minFrameGapMs", match.MinFrameGapMs, 0, 60_000);
        CheckRange(failures, "match.randomizeAmongTop", match.RandomizeAmongTop, 1, 20);
        if (!IdentityAggregationRules.All.Contains(match.Aggregation))
            failures.Add("match.aggregation must be a known rule.");

        CheckThreshold(failures, "match.accept", match.Accept);
        CheckThreshold(failures, "match.reviewFloor", match.ReviewFloor);
        CheckThreshold(failures, "match.minEachFrame", match.MinEachFrame);
        if (match.ReviewFloor > match.Accept)
            failures.Add("match.reviewFloor must not be above match.accept.");

        // The server chooses the frames out of the validated sequence: the shortest valid sequence must hold as many as the policy asks for.
        if (liveness.Frames > sequence.MinFrames)
            failures.Add("presence.liveness.frames must not be above presence.sequence.minFrames.");

        if (match.Frames > sequence.MinFrames)
            failures.Add("match.frames must not be above presence.sequence.minFrames.");

        CheckRange(failures, "closure.guidedRetries", policy.Closure.GuidedRetries, 0, 1);
        return failures;
    }

    private IdentityPolicySnapshot Load()
    {
        try
        {
            return LoadCore();
        }
        catch (Exception ex)
        {
            // Never an exception to a caller (ADR-013-R18): whatever went wrong, the policy is unavailable and a person decides. The type only, never the message.
            _logger.LogError("The identity policy could not be loaded ({ErrorType}); every verification goes to a person", ex.GetType().Name);
            return Unavailable(IdentityPolicyReasonCodes.PolicyInvalid);
        }
    }

    private IdentityPolicySnapshot LoadCore()
    {
        var settings = _options.Value;
        var path = ResolvePath(settings.PolicyFile);
        if (path is null || !File.Exists(path))
        {
            _logger.LogWarning("The identity policy is unavailable: reason={Reason}", IdentityPolicyReasonCodes.PolicyMissing);
            return Unavailable(IdentityPolicyReasonCodes.PolicyMissing);
        }

        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                _logger.LogWarning("The identity policy is unavailable: reason={Reason}", IdentityPolicyReasonCodes.PolicyInvalid);
                return Unavailable(IdentityPolicyReasonCodes.PolicyInvalid);
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The identity policy is unavailable: reason={Reason} ({ErrorType})", IdentityPolicyReasonCodes.PolicyMissing, ex.GetType().Name);
            return Unavailable(IdentityPolicyReasonCodes.PolicyMissing);
        }

        // What is validated and used is the canonical text, the very text the hash covers: nothing the file says escapes the hash.
        if (!IdentityConfigurationJson.TryCanonicalize(bytes, out var canonical))
        {
            _logger.LogWarning("The identity policy is unavailable: reason={Reason} (not strict JSON)", IdentityPolicyReasonCodes.PolicyInvalid);
            return Unavailable(IdentityPolicyReasonCodes.PolicyInvalid);
        }

        IdentityDecisionPolicy? policy;
        try
        {
            policy = IdentityConfigurationJson.ReadStrict<IdentityDecisionPolicy>(canonical);
        }
        catch (JsonException ex)
        {
            // The path of the member (for example $.match.accept), never its value.
            _logger.LogWarning("The identity policy is unavailable: reason={Reason} (member {Member})", IdentityPolicyReasonCodes.PolicyInvalid, ex.Path ?? "$");
            return Unavailable(IdentityPolicyReasonCodes.PolicyInvalid);
        }

        if (policy is null)
            return Unavailable(IdentityPolicyReasonCodes.PolicyInvalid);

        var failures = Validate(policy);
        if (failures.Count > 0)
        {
            foreach (var failure in failures)
                _logger.LogWarning("The identity policy is invalid: {Failure}", failure);

            _logger.LogWarning("The identity policy is unavailable: reason={Reason}", IdentityPolicyReasonCodes.PolicyInvalid);
            return Unavailable(IdentityPolicyReasonCodes.PolicyInvalid);
        }

        var hash = IdentityConfigurationJson.Sha256Hex(canonical);
        var pinned = settings.PolicySha256?.Trim();
        if (string.IsNullOrEmpty(pinned))
        {
            _logger.LogWarning(
                "The identity policy is unavailable: reason={Reason} (version {Version}, canonical hash {Hash})",
                IdentityPolicyReasonCodes.PolicyHashNotConfigured,
                policy.Version,
                hash);
            return Unavailable(IdentityPolicyReasonCodes.PolicyHashNotConfigured);
        }

        if (!string.Equals(pinned, hash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "The identity policy is unavailable: reason={Reason} (version {Version}, canonical hash {Hash})",
                IdentityPolicyReasonCodes.PolicyHashMismatch,
                policy.Version,
                hash);
            return Unavailable(IdentityPolicyReasonCodes.PolicyHashMismatch);
        }

        _logger.LogInformation("The identity policy is ready: version={Version} hash={Hash} provisional={Provisional}", policy.Version, hash, policy.Provisional);
        return new IdentityPolicySnapshot(IdentityPolicyState.Ready, null, policy, hash, policy.Provisional);
    }

    private string? ResolvePath(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var value = configured.Trim();
        return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(_baseDirectory, value));
    }

    private static IdentityPolicySnapshot Unavailable(string reasonCode)
    {
        return new IdentityPolicySnapshot(IdentityPolicyState.Unavailable, reasonCode, null, null, Provisional: true);
    }

    private static void CheckRange(List<string> failures, string name, int value, int min, int max)
    {
        if (value < min || value > max)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max}."));
    }

    /// <summary>A threshold, a ratio or a share that must be above 0 and at most 1.</summary>
    private static void CheckThreshold(List<string> failures, string name, double value)
    {
        if (!(value > 0 && value <= 1))
            failures.Add($"{name} must be above 0 and at most 1.");
    }

    /// <summary>A share that may be 0 (none) up to 1 (all).</summary>
    private static void CheckShare(List<string> failures, string name, double value)
    {
        if (!(value >= 0 && value <= 1))
            failures.Add($"{name} must be between 0 and 1.");
    }

    private static void CheckDegrees(List<string> failures, string name, double value)
    {
        if (!(value > 0 && value <= 90))
            failures.Add($"{name} must be above 0 and at most 90.");
    }

    private static void CheckVersionName(List<string> failures, string name, string? value)
    {
        if (value is null || value.Length is 0 or > 50 || !IsLowerNameCharacter(value[0]) || !value.All(character => IsLowerNameCharacter(character) || character is '.' or '-' or '_'))
            failures.Add($"{name} must be 1 to 50 lower case letters, digits, dots, hyphens or underscores, starting with a letter or a digit.");
    }

    private static void CheckDistinct(List<string> failures, string name, IReadOnlyList<string> values, Func<string, bool> isKnown)
    {
        if (values.Any(value => value is null || !isKnown(value)))
            failures.Add($"{name} must hold only known values.");

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            failures.Add($"{name} must not repeat a value.");
    }

    /// <summary>A name of a field of a document: a lower case letter, then letters and digits, at most 40 characters.</summary>
    private static bool IsMemberName(string value)
    {
        return value.Length is > 0 and <= 40 && char.IsAsciiLetterLower(value[0]) && value.All(char.IsAsciiLetterOrDigit);
    }

    private static bool IsLowerNameCharacter(char character)
    {
        return char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character);
    }
}
