using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Identity;
using Casazen.Core.Identity.Statistics;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Qualification;

public sealed record QualificationBoundedRate(long Count, long Total, double? Rate, double UpperBound)
{
    public static QualificationBoundedRate Of(CountedRate rate, double confidence)
    {
        return new QualificationBoundedRate(rate.Count, rate.Total, rate.Rate, IdentityStatistics.ClopperPearsonUpper(rate.Count, rate.Total, confidence));
    }

    public static QualificationBoundedRate Of(long count, long total, double confidence) => Of(new CountedRate(count, total), confidence);
}

public sealed record QualificationWilsonRate(long Count, long Total, double? Rate, double Low, double High)
{
    public static QualificationWilsonRate Of(long count, long total, double confidence)
    {
        var (low, high) = IdentityStatistics.Wilson(count, total, confidence);
        return new QualificationWilsonRate(count, total, new CountedRate(count, total).Rate, low, high);
    }
}

public sealed record QualificationClassRate(string Class, QualificationBoundedRate Rate);

public sealed record QualificationFieldRate(string Field, QualificationWilsonRate Rate);

public sealed record QualificationLatency(string Stage, int Count, double P50Ms, double P95Ms, double MaxMs);

public sealed record QualificationFormatDocumentRate(string Format, long Genuine, long GenuineRefused, long Hostile, long HostileLetThrough);

public sealed record QualificationDocumentMetrics(QualificationBoundedRate FalseRejects, QualificationBoundedRate HostileLetThrough, IReadOnlyList<QualificationFormatDocumentRate> ByFormat);

/// <summary>
/// The false approvals of the policy on the cases that must never be approved (an attack, a person with the document of another, an altered or a foreign document): how many
/// distinct cases (<see cref="Total"/>), and how many of each kind (<see cref="ByKind"/>; a case can be of two kinds, so the kinds can add up to more than the total). The target is zero.
/// <see cref="Decided"/> is the DENOMINATOR of each kind (a code of <see cref="QualificationCriticalKinds"/>): the cases of the kind the policy took a decision on (a document and a sequence
/// that ran to the end). A kind with none was not measured, and «no approval» of it proves nothing.
/// </summary>
public sealed record QualificationCriticalApprovals(int Total, IReadOnlyDictionary<string, int> ByKind, IReadOnlyDictionary<string, int> Decided)
{
    /// <summary>The kinds of critical case the policy took no decision on at all: the criterion cannot be measured for them (a method, so that it is not a member of the file).</summary>
    public IReadOnlyList<string> NotMeasured()
    {
        return [.. QualificationCriticalKinds.All.Where(kind => Decided.GetValueOrDefault(kind) == 0)];
    }
}

public sealed record QualificationClassFaults(string Class, long NotJudged, long Total);

public sealed record QualificationOperatingPoint(double Threshold, long FalseAccepts, long Negatives, long FalseRejects, long Positives);

public sealed record QualificationCandidateComparison(
    string Measure,
    string Reference,
    string Candidate,
    QualificationOperatingPoint ReferencePoint,
    QualificationOperatingPoint CandidatePoint,
    bool CandidateRejectsFewer);

/// <summary>
/// The aggregate metrics of a run at the thresholds of the policy in force: the false match and false non-match rates of the comparison of the face (the decision score is the
/// aggregate similarity, or 0 when a chosen frame is under its minimum), the attack and bona fide classification errors of the presence, the exactness of each field of the
/// documents, the shadow automation rate, the document stage, the critical false approvals and the time of each stage. Every rate has its count and total.
/// <see cref="Fmr"/> counts every comparison; <see cref="FmrCouples"/> counts the DISTINCT couples of persons (the person present and the owner of the document): a couple of persons is one
/// piece of evidence however many captures of them were compared, and it is a false match when any of its comparisons is. The verdict of the false match rate is the second.
/// <see cref="Apcer"/> is over the attacks that were judged; <see cref="AttacksNotJudged"/> says how many of each class were not.
/// </summary>
public sealed record QualificationMetricsBlock(
    QualificationBoundedRate Fmr,
    QualificationBoundedRate FmrCouples,
    QualificationBoundedRate Fnmr,
    IReadOnlyList<QualificationClassRate> Apcer,
    double? ApcerMax,
    IReadOnlyList<QualificationClassFaults> AttacksNotJudged,
    QualificationBoundedRate Bpcer,
    IReadOnlyList<QualificationFieldRate> OcrExactness,
    QualificationWilsonRate ShadowAutomation,
    QualificationDocumentMetrics Document,
    QualificationCriticalApprovals CriticalFalseApprovals,
    IReadOnlyList<QualificationLatency> Latency,
    IReadOnlyList<QualificationCandidateComparison> CandidateComparisons);

public static class QualificationMetrics
{
    public const string MrzFieldSuffix = ".mrz";

    public static IReadOnlyList<string> CoreAttackClasses { get; } =
        [QualificationAttackClasses.PhotoPrint, QualificationAttackClasses.ScreenReplay, QualificationAttackClasses.VideoReplay, QualificationAttackClasses.Synthetic];

    public static QualificationMetricsBlock Rebound(QualificationMetricsBlock block, double confidence)
    {
        ArgumentNullException.ThrowIfNull(block);

        QualificationBoundedRate Bounded(QualificationBoundedRate rate) => QualificationBoundedRate.Of(rate.Count, rate.Total, confidence);
        QualificationWilsonRate Wilson(QualificationWilsonRate rate) => QualificationWilsonRate.Of(rate.Count, rate.Total, confidence);
        return block with
        {
            Fmr = Bounded(block.Fmr),
            FmrCouples = Bounded(block.FmrCouples),
            Fnmr = Bounded(block.Fnmr),
            Apcer = [.. block.Apcer.Select(entry => entry with { Rate = Bounded(entry.Rate) })],
            Bpcer = Bounded(block.Bpcer),
            OcrExactness = [.. block.OcrExactness.Select(entry => entry with { Rate = Wilson(entry.Rate) })],
            ShadowAutomation = Wilson(block.ShadowAutomation),
            Document = block.Document with { FalseRejects = Bounded(block.Document.FalseRejects), HostileLetThrough = Bounded(block.Document.HostileLetThrough) },
        };
    }

    /// <summary>
    /// The score a comparison is judged on: the similarity of the chosen frames combined by the rule of the policy, or 0 when a chosen frame is under <paramref name="minEachFrame"/>
    /// (the policy accepts only when both hold, so a comparison that fails the second never reaches an acceptance whatever the first is). It is what <see cref="IdentityStatistics.Fmr"/> and
    /// <see cref="IdentityStatistics.Fnmr"/> take with the acceptance threshold of the policy, so the two rates are the ones of the whole rule.
    /// </summary>
    public static double DecisionScore(double aggregate, double minSimilarity, double minEachFrame)
    {
        return minSimilarity >= minEachFrame ? aggregate : 0.0;
    }

    public static double Aggregate(string rule, IReadOnlyList<double> scores)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(scores);
        if (scores.Count == 0)
            throw new ArgumentException("At least one score is needed.", nameof(scores));

        return rule switch
        {
            IdentityAggregationRules.Median => Median(scores),
            IdentityAggregationRules.Min => scores.Min(),
            IdentityAggregationRules.Mean => scores.Average(),
            _ => throw new ArgumentException("The aggregation rule is not known.", nameof(rule)),
        };
    }

    public static IReadOnlyList<NamedRate> ExactnessRates(IEnumerable<(string Field, string? Expected, string? Read)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return IdentityStatistics.FieldExactness(pairs.Select(pair => (pair.Field, pair.Expected, SpelledAsExpected(pair.Field, pair.Expected, pair.Read))), NormalizeField);
    }

    /// <summary>
    /// The value read, or the true value when the two are the same NAME spelled in the other way the MRZ may spell it: the rule of the coherence checker (a printed name must equal the one of the
    /// MRZ with the letters of the umlauts written as the MRZ writes them, or with their diacritics only dropped), through <c>MrzCheckInConsistency.Tokens</c>, which holds it. So «Müller» read as
    /// <c>MULLER</c> is as exact as when it is read as <c>MUELLER</c> (<see cref="NormalizeField"/> brings that one to the same form); a field that is not a name, or one that is not read, is
    /// as it was.
    /// </summary>
    public static string? SpelledAsExpected(string field, string? expected, string? read)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (expected is null || read is null || !IsNameField(field))
            return read;

        var wanted = NameKey(expected, transliterate: false);
        return wanted.Length > 0 && string.Equals(wanted, NameKey(read, transliterate: false), StringComparison.Ordinal) ? expected : read;
    }

    /// <summary>
    /// The opaque key of a couple of persons, the person present and the owner of the document they present (an ordered couple, as the validator counts the distinct impostor pairs): 24 hexadecimal
    /// characters of the SHA-256 of the two ids, so the file of the results says which comparisons are about the same two persons and not who they are. The ids of the manifest are anonymous
    /// already; this is not a protection of a secret.
    /// </summary>
    public static string CoupleKey(string person, string owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(person);
        ArgumentException.ThrowIfNullOrEmpty(owner);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("couple|" + person + "|" + owner)).AsSpan(0, 12));
    }

    public static bool WasJudged(QualificationCaseResult candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Error is null
            && candidate.Presence is { IntakeError: null, ValidationUnavailable: false } presence
            && !string.Equals(presence.LivenessState, QualificationDecisionCodes.LivenessUnavailable, StringComparison.Ordinal);
    }

    public static string? NormalizeField(string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (value is null)
            return null;

        var name = field.EndsWith(MrzFieldSuffix, StringComparison.Ordinal) ? field[..^MrzFieldSuffix.Length] : field;
        switch (name)
        {
            case ZonedFieldNames.Surname:
            case ZonedFieldNames.GivenNames:
            case ZonedFieldNames.BirthPlace:
                return NameKey(value, transliterate: true);
            case ZonedFieldNames.BirthDate:
            case ZonedFieldNames.IssueDate:
            case ZonedFieldNames.ExpiryDate:
                return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : value.Trim().ToUpperInvariant();
            case ZonedFieldNames.Sex:
            case ZonedFieldNames.Nationality:
                return new string(value.ToUpperInvariant().Where(character => character is >= 'A' and <= 'Z').ToArray());
            default:
                return new string(value.ToUpperInvariant().Where(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9').ToArray());
        }
    }

    public static QualificationMetricsBlock Compute(
        IReadOnlyList<QualificationCaseResult> cases,
        IReadOnlyList<QualificationImpostorTrial> trials,
        QualificationThresholds thresholds,
        double confidence,
        IReadOnlyList<QualificationModelInfo> models)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(trials);
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(models);

        var impostorScores = ImpostorDecisionScores(cases, trials, thresholds.MinEachFrame);
        var genuineScores = GenuineDecisionScores(cases, thresholds.MinEachFrame);
        var fmr = QualificationBoundedRate.Of(IdentityStatistics.Fmr(impostorScores, thresholds.Accept), confidence);
        var fmrCouples = QualificationBoundedRate.Of(ImpostorCouples(cases, trials, thresholds.Accept, thresholds.MinEachFrame), confidence);
        var fnmr = QualificationBoundedRate.Of(IdentityStatistics.Fnmr(genuineScores, thresholds.Accept), confidence);

        var attackOutcomes = new Dictionary<string, List<bool>>(StringComparer.Ordinal);
        var attackFaults = new SortedDictionary<string, (long NotJudged, long Total)>(StringComparer.Ordinal);
        foreach (var attackClass in QualificationAttackClasses.All)
        {
            attackOutcomes[attackClass] = [];
            attackFaults[attackClass] = (0, 0);
        }

        foreach (var attack in cases.Where(candidate => candidate.Kind == QualificationCaseKinds.Attack && candidate.AttackClass is not null && candidate.Presence is not null))
        {
            if (!attackOutcomes.TryGetValue(attack.AttackClass!, out var outcomes))
                attackOutcomes[attack.AttackClass!] = outcomes = [];

            attackFaults.TryGetValue(attack.AttackClass!, out var seen);
            var judged = WasJudged(attack);
            attackFaults[attack.AttackClass!] = (seen.NotJudged + (judged ? 0 : 1), seen.Total + 1);
            if (judged)
                outcomes.Add(attack.Presence!.Accepted);
        }

        var apcer = IdentityStatistics.Apcer<List<bool>>(attackOutcomes);
        var bonaFide = cases
            .Where(candidate => candidate.Kind is QualificationCaseKinds.Genuine or QualificationCaseKinds.ImpostorPair && candidate.Presence is not null)
            .Select(candidate => candidate.Presence!.Accepted)
            .ToList();

        return new QualificationMetricsBlock(
            fmr,
            fmrCouples,
            fnmr,
            [.. apcer.ByClass.Select(entry => new QualificationClassRate(entry.Name, QualificationBoundedRate.Of(entry.Rate, confidence)))],
            apcer.Max,
            [.. attackFaults.Select(entry => new QualificationClassFaults(entry.Key, entry.Value.NotJudged, entry.Value.Total))],
            QualificationBoundedRate.Of(IdentityStatistics.Bpcer(bonaFide), confidence),
            Exactness(cases, confidence),
            Automation(cases, confidence),
            DocumentStage(cases, confidence),
            CriticalApprovals(cases),
            Latency(cases, trials),
            Comparisons(cases, trials, thresholds, models));
    }

    public static List<double> ImpostorDecisionScores(IReadOnlyList<QualificationCaseResult> cases, IReadOnlyList<QualificationImpostorTrial> trials, double minEachFrame)
    {
        var scores = new List<double>();
        foreach (var impostor in cases.Where(candidate => candidate.Kind == QualificationCaseKinds.ImpostorPair))
        {
            if (impostor.Presence is { Similarity: { } similarity, MinSimilarity: { } min })
                scores.Add(DecisionScore(similarity, min, minEachFrame));
        }

        foreach (var trial in trials)
            scores.Add(DecisionScore(trial.Similarity, trial.MinSimilarity, minEachFrame));

        return scores;
    }

    public static CountedRate ImpostorCouples(IReadOnlyList<QualificationCaseResult> cases, IReadOnlyList<QualificationImpostorTrial> trials, double accept, double minEachFrame)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(trials);

        var matched = new Dictionary<string, bool>(StringComparer.Ordinal);
        void Add(string couple, double score)
        {
            matched[couple] = matched.GetValueOrDefault(couple) || score >= accept;
        }

        foreach (var impostor in cases.Where(candidate => candidate.Kind == QualificationCaseKinds.ImpostorPair))
        {
            if (impostor.Presence is { Similarity: { } similarity, MinSimilarity: { } min })
                Add(impostor.Couple ?? "case:" + impostor.Id, DecisionScore(similarity, min, minEachFrame));
        }

        foreach (var trial in trials)
            Add(trial.Couple ?? "trial:" + trial.SequenceCase + ">" + trial.DocumentCase, DecisionScore(trial.Similarity, trial.MinSimilarity, minEachFrame));

        return new CountedRate(matched.Count(entry => entry.Value), matched.Count);
    }

    public static List<double> GenuineDecisionScores(IReadOnlyList<QualificationCaseResult> cases, double minEachFrame)
    {
        var scores = new List<double>();
        foreach (var genuine in cases.Where(IsGenuineOwn))
        {
            if (genuine.Presence is { Similarity: { } similarity, MinSimilarity: { } min })
                scores.Add(DecisionScore(similarity, min, minEachFrame));
        }

        return scores;
    }

    public static bool IsGenuineOwn(QualificationCaseResult candidate)
    {
        return candidate.Kind == QualificationCaseKinds.Genuine && !candidate.Altered && !candidate.Foreign;
    }

    private static List<QualificationFieldRate> Exactness(IReadOnlyList<QualificationCaseResult> cases, double confidence)
    {
        var totals = new SortedDictionary<string, (long Exact, long Total)>(StringComparer.Ordinal);
        foreach (var candidate in cases.Where(candidate => IsGenuineOwn(candidate) && candidate.Document is not null))
        {
            foreach (var field in candidate.Document!.Fields)
            {
                totals.TryGetValue(field.Field, out var seen);
                totals[field.Field] = (seen.Exact + field.Exact, seen.Total + field.Total);
            }
        }

        return [.. totals.Select(entry => new QualificationFieldRate(entry.Key, QualificationWilsonRate.Of(entry.Value.Exact, entry.Value.Total, confidence)))];
    }

    private static QualificationWilsonRate Automation(IReadOnlyList<QualificationCaseResult> cases, double confidence)
    {
        var checkIns = cases.Where(candidate => IsGenuineOwn(candidate) && candidate.Document is not null && candidate.Presence is not null).ToList();
        var approved = checkIns.Count(candidate => candidate.Decision?.Shadow == QualificationDecisionCodes.ShadowApprove);
        return QualificationWilsonRate.Of(approved, checkIns.Count, confidence);
    }

    private static QualificationDocumentMetrics DocumentStage(IReadOnlyList<QualificationCaseResult> cases, double confidence)
    {
        var genuine = cases.Where(candidate => IsGenuineOwn(candidate) && candidate.Document is not null).ToList();
        var hostile = cases.Where(candidate => (candidate.Altered || candidate.Foreign) && candidate.Document is not null).ToList();
        var refused = genuine.Count(candidate => !candidate.Document!.Complete);
        var letThrough = hostile.Count(candidate => candidate.Document!.Complete && candidate.Document.Coherence == QualificationDecisionCodes.CoherencePassed);

        var byFormat = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        long[] CountsOf(QualificationCaseResult candidate)
        {
            var format = candidate.Format ?? "unknown";
            if (!byFormat.TryGetValue(format, out var counts))
                byFormat[format] = counts = new long[4];

            return counts;
        }

        foreach (var candidate in genuine)
        {
            var counts = CountsOf(candidate);
            counts[0]++;
            if (!candidate.Document!.Complete)
                counts[1]++;
        }

        foreach (var candidate in hostile)
        {
            var counts = CountsOf(candidate);
            counts[2]++;
            if (candidate.Document!.Complete && candidate.Document.Coherence == QualificationDecisionCodes.CoherencePassed)
                counts[3]++;
        }

        return new QualificationDocumentMetrics(
            QualificationBoundedRate.Of(refused, genuine.Count, confidence),
            QualificationBoundedRate.Of(letThrough, hostile.Count, confidence),
            [.. byFormat.Select(entry => new QualificationFormatDocumentRate(entry.Key, entry.Value[0], entry.Value[1], entry.Value[2], entry.Value[3]))]);
    }

    private static QualificationCriticalApprovals CriticalApprovals(IReadOnlyList<QualificationCaseResult> cases)
    {
        var approved = cases.Where(candidate => candidate.Decision?.Final == QualificationDecisionCodes.FinalAutoApproved).ToList();
        var critical = approved.Where(candidate => candidate.Kind != QualificationCaseKinds.Genuine || candidate.Altered || candidate.Foreign).ToList();
        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            [QualificationCriticalKinds.Attack] = critical.Count(candidate => candidate.Kind == QualificationCaseKinds.Attack),
            [QualificationCriticalKinds.ImpostorPair] = critical.Count(candidate => candidate.Kind == QualificationCaseKinds.ImpostorPair),
            [QualificationCriticalKinds.Altered] = critical.Count(candidate => candidate.Altered),
            [QualificationCriticalKinds.Foreign] = critical.Count(candidate => candidate.Foreign),
        };

        var decided = cases.Where(candidate => candidate.Decision is not null).ToList();
        var decidedByKind = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            [QualificationCriticalKinds.Attack] = decided.Count(candidate => candidate.Kind == QualificationCaseKinds.Attack),
            [QualificationCriticalKinds.ImpostorPair] = decided.Count(candidate => candidate.Kind == QualificationCaseKinds.ImpostorPair),
            [QualificationCriticalKinds.Altered] = decided.Count(candidate => candidate.Altered),
            [QualificationCriticalKinds.Foreign] = decided.Count(candidate => candidate.Foreign),
        };
        return new QualificationCriticalApprovals(
            critical.Count,
            new Dictionary<string, int>(byKind, StringComparer.Ordinal),
            new Dictionary<string, int>(decidedByKind, StringComparer.Ordinal));
    }

    private static List<QualificationLatency> Latency(IReadOnlyList<QualificationCaseResult> cases, IReadOnlyList<QualificationImpostorTrial> trials)
    {
        var latency = new List<QualificationLatency>();
        foreach (var stage in QualificationStages.All)
        {
            var values = cases.Where(candidate => candidate.StageMs.ContainsKey(stage)).Select(candidate => candidate.StageMs[stage]).ToList();
            if (stage == QualificationStages.PresenceMatch)
                values.AddRange(trials.Select(trial => trial.MatchMs));

            if (values.Count == 0)
                continue;

            latency.Add(new QualificationLatency(stage, values.Count, IdentityStatistics.Percentile(values, 50)!.Value, IdentityStatistics.Percentile(values, 95)!.Value, values.Max()));
        }

        return latency;
    }

    private static List<QualificationCandidateComparison> Comparisons(
        IReadOnlyList<QualificationCaseResult> cases,
        IReadOnlyList<QualificationImpostorTrial> trials,
        QualificationThresholds thresholds,
        IReadOnlyList<QualificationModelInfo> models)
    {
        var comparisons = new List<QualificationCandidateComparison>();

        var positives = new List<(double Reference, double Candidate)>();
        var negatives = new List<(double Reference, double Candidate)>();
        foreach (var genuine in cases.Where(IsGenuineOwn))
            AddPair(positives, genuine.PairedMatch, thresholds.MatchAggregation, false);

        foreach (var impostor in cases.Where(candidate => candidate.Kind == QualificationCaseKinds.ImpostorPair))
            AddPair(negatives, impostor.PairedMatch, thresholds.MatchAggregation, false);

        foreach (var trial in trials)
            AddPair(negatives, trial.Paired, thresholds.MatchAggregation, false);

        if (CompareAtTheSameAcceptances("match", Name(models, BiometricModelRoles.FaceRecognition), Name(models, BiometricModelRoles.FaceRecognitionCandidate), thresholds.Accept, positives, negatives) is { } match)
            comparisons.Add(match);

        positives = [];
        negatives = [];
        foreach (var present in cases.Where(candidate => candidate.Kind is QualificationCaseKinds.Genuine or QualificationCaseKinds.ImpostorPair))
            AddPair(positives, present.PairedLiveness, IdentityAggregationRules.Min, true);

        foreach (var attack in cases.Where(candidate => candidate.Kind == QualificationCaseKinds.Attack))
            AddPair(negatives, attack.PairedLiveness, IdentityAggregationRules.Min, true);

        if (CompareAtTheSameAcceptances("liveness", Name(models, BiometricModelRoles.AntiSpoofing), Name(models, BiometricModelRoles.AntiSpoofingCandidate), thresholds.LivenessMinScore, positives, negatives) is { } liveness)
            comparisons.Add(liveness);

        return comparisons;
    }

    private static void AddPair(List<(double Reference, double Candidate)> into, QualificationPairedScores? paired, string rule, bool lowestOnly)
    {
        if (paired is not { Candidate: { Count: > 0 } candidate } || paired.Reference.Count == 0)
            return;

        var effective = lowestOnly ? IdentityAggregationRules.Min : rule;
        into.Add((Aggregate(effective, paired.Reference), Aggregate(effective, candidate)));
    }

    public static QualificationCandidateComparison? CompareAtTheSameAcceptances(
        string measure,
        string reference,
        string candidate,
        double referenceThreshold,
        IReadOnlyList<(double Reference, double Candidate)> positives,
        IReadOnlyList<(double Reference, double Candidate)> negatives)
    {
        ArgumentNullException.ThrowIfNull(positives);
        ArgumentNullException.ThrowIfNull(negatives);
        if (negatives.Count == 0 || positives.Count == 0)
            return null;

        var referenceAccepted = negatives.Count(negative => negative.Reference >= referenceThreshold);
        var referenceRejected = positives.Count(positive => positive.Reference < referenceThreshold);

        var descending = negatives.Select(negative => negative.Candidate).OrderByDescending(score => score).ToList();
        var candidateThreshold = referenceAccepted >= descending.Count ? 0.0 : Math.BitIncrement(descending[referenceAccepted]);
        var candidateAccepted = negatives.Count(negative => negative.Candidate >= candidateThreshold);
        var candidateRejected = positives.Count(positive => positive.Candidate < candidateThreshold);

        return new QualificationCandidateComparison(
            measure,
            reference,
            candidate,
            new QualificationOperatingPoint(referenceThreshold, referenceAccepted, negatives.Count, referenceRejected, positives.Count),
            new QualificationOperatingPoint(candidateThreshold, candidateAccepted, negatives.Count, candidateRejected, positives.Count),
            candidateRejected < referenceRejected);
    }

    private static string Name(IReadOnlyList<QualificationModelInfo> models, string role)
    {
        var model = models.FirstOrDefault(candidate => string.Equals(candidate.Role, role, StringComparison.Ordinal));
        return model is null ? role : model.Name + " " + model.Version;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string NameKey(string value, bool transliterate)
    {
        return string.Concat(MrzCheckInConsistency.Tokens(value, transliterate));
    }

    private static bool IsNameField(string field)
    {
        var name = field.EndsWith(MrzFieldSuffix, StringComparison.Ordinal) ? field[..^MrzFieldSuffix.Length] : field;
        return name is ZonedFieldNames.Surname or ZonedFieldNames.GivenNames or ZonedFieldNames.BirthPlace;
    }
}

public static class QualificationDecisionCodes
{
    public const string FinalReview = "review";
    public const string FinalGuidedRetry = "guided_retry";
    public const string FinalAutoApproved = "auto_approved";
    public const string ShadowReview = "review";
    public const string ShadowApprove = "approve";
    public const string CoherencePassed = "passed";
    public const string CoherenceIdentifyingConflict = "identifying_conflict";
    public const string CoherenceMrzInvalid = "mrz_invalid";
    public const string CoherenceExpired = "expired";
    public const string CoherenceBookingConflict = "booking_conflict";
    public const string CoherenceUnknown = "unknown";

    public const string LivenessPassed = "passed";
    public const string LivenessFailed = "failed";
    public const string LivenessUnavailable = "unavailable";
    public const string NotRun = "not_run";
    public const string MatchOk = "ok";
    public const string MatchNoPortrait = "no_portrait";
    public const string MatchUnavailable = "unavailable";
}
