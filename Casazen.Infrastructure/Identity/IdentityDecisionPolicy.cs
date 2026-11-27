namespace Casazen.Infrastructure.Identity;

// E70/F01/US02/T01 (NF-193, NF-194, ADR-013-R18): the decision policy of the self-hosted identity verification, as its file spells it. These records hold NO number
// and NO default of their own: every threshold, limit and rule is read from the versioned file (Casazen.Web/IdentityPolicies/identity-policy-*.json), which the
// IdentityPolicyProvider loads once, validates and pins by its canonical SHA-256. The names are the names of the members of the file, in camel case.

/// <summary>
/// The decision policy: every threshold and aggregation rule of the self-hosted verification, versioned and frozen by hash. A qualified file is never edited
/// (see <see cref="IdentityPolicyProvider"/>); a new calibration is a new file with its own version and hash.
/// </summary>
/// <param name="Version">The version of the policy, for example <c>identity-policy-2026.11-1</c>: the name of its file without the extension. What the log rows and the qualified segments record.</param>
/// <param name="Provisional">
/// True while the numbers are the provisional defaults, not calibrated on real data: a provisional policy can NEVER be qualified for a segment, so nothing is
/// approved automatically until a calibrated policy file is committed.
/// </param>
/// <param name="CaptureRules">The version of the rules of the quality of a capture the policy was written for (<c>CaptureQualityAssessor.Version</c>), for example <c>capture-2026.11-1</c>.</param>
/// <param name="Document">What is asked of the document.</param>
/// <param name="Presence">What is asked of the presence of the guest.</param>
/// <param name="Match">How the portrait of the document is compared with the face of the guest.</param>
/// <param name="Closure">How a verification is closed.</param>
public sealed record IdentityDecisionPolicy(
    string Version,
    bool Provisional,
    string CaptureRules,
    IdentityDocumentPolicy Document,
    IdentityPresencePolicy Presence,
    IdentityMatchPolicy Match,
    IdentityClosurePolicy Closure);

/// <summary>The rules of the document step.</summary>
/// <param name="MaxRecapturesPerSide">How many times a side of the document may be captured again after a targeted guidance.</param>
/// <param name="IdentifyingFields">
/// The fields whose conflict between the two sides (or with the MRZ) sends the verification to a person (NF-190): names in the vocabulary of the fields of the
/// format files, for example <c>surname</c>.
/// </param>
/// <param name="PerceptualDuplicateMaxDistance">The largest distance between the perceptual hashes of two pictures for them to be the same side photographed twice (<c>document_side_duplicate</c>).</param>
public sealed record IdentityDocumentPolicy(int MaxRecapturesPerSide, IReadOnlyList<string> IdentifyingFields, int PerceptualDuplicateMaxDistance);

/// <summary>The rules of the presence of the guest: the challenge, the sequence of frames, its continuity, the pose of the head and the liveness.</summary>
/// <param name="Challenge">The challenge the server issues.</param>
/// <param name="Sequence">What the sequence of frames must look like.</param>
/// <param name="Continuity">What keeps a sequence from being a still picture or the splice of two people.</param>
/// <param name="Pose">How far the head must turn for a move to count.</param>
/// <param name="Liveness">How the anti-spoofing model judges the frames the server chose.</param>
public sealed record IdentityPresencePolicy(
    IdentityChallengePolicy Challenge,
    IdentitySequencePolicy Sequence,
    IdentityContinuityPolicy Continuity,
    IdentityPosePolicy Pose,
    IdentityLivenessPolicy Liveness);

/// <summary>The unpredictable challenge of the presence (NF-192): its entropy is at least <see cref="IdentityPolicyProvider.MinChallengeEntropyBits"/> bits, whatever the numbers.</summary>
/// <param name="Steps">How many moves it asks for.</param>
/// <param name="Actions">The moves it draws from, codes of <c>PresenceActions</c>.</param>
/// <param name="NoImmediateRepeat">True when a move never follows itself.</param>
/// <param name="LifetimeSeconds">How long a challenge lives before it expires.</param>
/// <param name="StepMs">The time the guest is given for each move, in milliseconds.</param>
/// <param name="LeadInMs">The time before the first move, in milliseconds.</param>
public sealed record IdentityChallengePolicy(
    int Steps,
    IReadOnlyList<string> Actions,
    bool NoImmediateRepeat,
    int LifetimeSeconds,
    int StepMs,
    int LeadInMs);

/// <summary>What a sequence of frames must look like to be valid (NF-192).</summary>
/// <param name="TargetFps">The rate the page captures at, in frames per second.</param>
/// <param name="MinFrames">The fewest frames a sequence may have.</param>
/// <param name="MaxFrames">The most frames a sequence may have.</param>
/// <param name="MinDurationMs">The shortest span between the first and the last frame, in milliseconds.</param>
/// <param name="MaxDurationMs">The longest span, in milliseconds.</param>
/// <param name="MaxGapMs">The longest gap between two consecutive frames, in milliseconds.</param>
/// <param name="MaxShareWithoutFace">The largest share (0 to 1) of the frames in which no face is found.</param>
/// <param name="DetectorMaxSide">The longest side, in pixels, a frame is scaled to for the face detector.</param>
public sealed record IdentitySequencePolicy(
    int TargetFps,
    int MinFrames,
    int MaxFrames,
    int MinDurationMs,
    int MaxDurationMs,
    int MaxGapMs,
    double MaxShareWithoutFace,
    int DetectorMaxSide);

/// <summary>What keeps a sequence from being a still picture or the splice of two people (NF-192).</summary>
/// <param name="MaxCenterJump">The largest move of the centre of the face between two frames, as a share of the frame.</param>
/// <param name="MaxSizeChange">The largest change of the size of the face between two frames, as a share of its size.</param>
/// <param name="MinPairSimilarity">The least similarity between the faces of the frames sampled from the sequence, in the scale of the matcher (0 to 1).</param>
/// <param name="PairSamples">How many pairs of frames are sampled.</param>
public sealed record IdentityContinuityPolicy(double MaxCenterJump, double MaxSizeChange, double MinPairSimilarity, int PairSamples);

/// <summary>How far the head must turn for a move of the challenge to count, in degrees (NF-192).</summary>
/// <param name="TurnMinYawDegrees">The least yaw of a turn to the left or to the right.</param>
/// <param name="LookMinPitchDegrees">The least pitch of a look up or down.</param>
/// <param name="NeutralMaxDegrees">The most the head may be away from the front for the neutral pose.</param>
public sealed record IdentityPosePolicy(double TurnMinYawDegrees, double LookMinPitchDegrees, double NeutralMaxDegrees);

/// <summary>How the anti-spoofing model judges the frames the server chose (NF-192, NF-193).</summary>
/// <param name="Frames">How many frames it runs on.</param>
/// <param name="MinScore">The least score of the real-face class (0 to 1), in the scale of the model.</param>
/// <param name="Rule">How the scores of the frames are combined, a code of <see cref="IdentityLivenessRules"/>.</param>
public sealed record IdentityLivenessPolicy(int Frames, double MinScore, string Rule);

/// <summary>
/// How the portrait of the document is compared with the frames the server chose (NF-193). The similarity is in the scale of the matcher of today, the cosine
/// between two face vectors clamped to 0..1. Presence and similarity are independent conditions: no value of one compensates a failure of the other.
/// </summary>
/// <param name="Frames">How many frames are compared.</param>
/// <param name="MinFrameGapMs">The least time between two chosen frames, in milliseconds.</param>
/// <param name="RandomizeAmongTop">The server draws each frame at random among this many of the best candidates.</param>
/// <param name="Aggregation">How the similarities of the frames are combined into one, a code of <see cref="IdentityAggregationRules"/>.</param>
/// <param name="Accept">The aggregated similarity from which the decision of the policy is approval.</param>
/// <param name="ReviewFloor">The aggregated similarity under which there is nothing for a person to review (never above <paramref name="Accept"/>).</param>
/// <param name="MinEachFrame">The least similarity every single chosen frame must reach.</param>
public sealed record IdentityMatchPolicy(
    int Frames,
    int MinFrameGapMs,
    int RandomizeAmongTop,
    string Aggregation,
    double Accept,
    double ReviewFloor,
    double MinEachFrame);

/// <summary>How a verification is closed (NF-194).</summary>
/// <param name="GuidedRetries">How many guided retries a correctable defect gets before the verification goes to a person (0 or 1).</param>
public sealed record IdentityClosurePolicy(int GuidedRetries);

/// <summary>The aggregation rules of <see cref="IdentityMatchPolicy.Aggregation"/>.</summary>
public static class IdentityAggregationRules
{
    public const string Median = "median";
    public const string Min = "min";
    public const string Mean = "mean";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Median, Min, Mean];
}

/// <summary>The rules of <see cref="IdentityLivenessPolicy.Rule"/>.</summary>
public static class IdentityLivenessRules
{
    /// <summary>Every frame the server chose must reach the minimum score.</summary>
    public const string AllFrames = "all";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [AllFrames];
}
