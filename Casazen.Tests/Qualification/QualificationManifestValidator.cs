using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Casazen.Core.Identity;

namespace Casazen.Tests.Qualification;

// E74/F01/US01/T02 (NF-198, D12): the checks a dataset manifest must pass before anybody runs a qualification on it. The validator answers with CODES and IDS
// only, never a value: an error names a code, the id of a person the manifest LISTS or of a capture with a valid id (else the position), and the member
// that failed; a reference to a person nobody lists is NOT echoed (the capture and the member say where). The counts are numbers. It checks that a file
// EXISTS and never opens one: the ground-truth files are read by the runner alone and never printed.
// Codes are never renamed or reused: a new one goes at the end of its list.

public static class QualificationManifestErrorCodes
{
    public const string FolderMissing = "manifest_folder_missing";

    public const string DatasetIdInvalid = "dataset_id_invalid";
    public const string VersionInvalid = "version_invalid";

    public const string RightsMissing = "rights_missing";

    public const string RightsPurposeMissing = "rights_purpose_missing";
    public const string RightsConsentReferenceMissing = "rights_consent_reference_missing";
    public const string RightsCollectedByMissing = "rights_collected_by_missing";

    public const string RightsTrainingUseNotFalse = "rights_training_use_not_false";

    public const string PersonsEmpty = "persons_empty";
    public const string PersonIdInvalid = "person_id_invalid";

    public const string PersonIdDuplicate = "person_id_duplicate";

    public const string PersonInTwoSplits = "person_in_two_splits";

    public const string PersonSplitUnknown = "person_split_unknown";
    public const string CapturesEmpty = "captures_empty";
    public const string CaptureIdInvalid = "capture_id_invalid";
    public const string CaptureIdDuplicate = "capture_id_duplicate";

    public const string CapturePersonUnknown = "capture_person_unknown";

    public const string CaptureSessionInvalid = "capture_session_invalid";
    public const string CaptureDeviceInvalid = "capture_device_invalid";
    public const string CaptureChannelUnknown = "capture_channel_unknown";
    public const string CaptureKindUnknown = "capture_kind_unknown";

    public const string CaptureEmpty = "capture_empty";

    public const string AttackClassMissing = "attack_class_missing";

    public const string AttackClassUnknown = "attack_class_unknown";

    public const string AttackClassOnGenuine = "attack_class_on_genuine";

    public const string DocumentFormatInvalid = "document_format_invalid";

    public const string DocumentOwnerUnknown = "document_owner_unknown";

    public const string DocumentOwnerSplitMismatch = "document_owner_split_mismatch";

    public const string SequenceChallengeInvalid = "sequence_challenge_invalid";

    public const string PathMissing = "path_missing";

    public const string PathInvalid = "path_invalid";

    public const string FileMissing = "file_missing";

    public const string SessionSharedAcrossSplits = "session_shared_across_splits";

    public const string DeviceSharedAcrossSplits = "device_shared_across_splits";

    public static IReadOnlyList<string> All { get; } =
    [
        FolderMissing, DatasetIdInvalid, VersionInvalid, RightsMissing, RightsPurposeMissing, RightsConsentReferenceMissing, RightsCollectedByMissing,
        RightsTrainingUseNotFalse, PersonsEmpty, PersonIdInvalid, PersonIdDuplicate, PersonInTwoSplits, PersonSplitUnknown, CapturesEmpty, CaptureIdInvalid,
        CaptureIdDuplicate, CapturePersonUnknown, CaptureSessionInvalid, CaptureDeviceInvalid, CaptureChannelUnknown, CaptureKindUnknown, CaptureEmpty,
        AttackClassMissing, AttackClassUnknown, AttackClassOnGenuine, DocumentFormatInvalid, DocumentOwnerUnknown, DocumentOwnerSplitMismatch,
        SequenceChallengeInvalid, PathMissing, PathInvalid, FileMissing, SessionSharedAcrossSplits, DeviceSharedAcrossSplits,
    ];
}

/// <summary>
/// One error of a manifest: a code, the person (one the manifest lists) and/or the capture (one with a valid id) it is about, and the member that failed
/// (<c>rights.purpose</c>, <c>document.front</c>). When the id itself is the problem, or a reference points at nobody, no id is echoed: <see cref="Field"/> says
/// where (<c>person</c>, or the position <c>captures[3]</c>). Never a value.
/// </summary>
public sealed record QualificationManifestError(string Code, string? PersonId = null, string? CaptureId = null, string? Field = null)
{
    public override string ToString()
    {
        var text = new StringBuilder(Code);
        if (PersonId is not null)
            text.Append(" person=").Append(PersonId);

        if (CaptureId is not null)
            text.Append(" capture=").Append(CaptureId);

        if (Field is not null)
            text.Append(" field=").Append(Field);

        return text.ToString();
    }
}

/// <summary>
/// The kinds of case that must never be approved, the ones the criterion «no false approval on a critical case» is measured on (the names the results use): an attack, a person
/// with the document of another one (an impostor pair), an altered document and a foreign image. A kind is measured only on WHOLE check-ins of it (a document and a sequence
/// together), because only those get a decision of the policy: a split with none of a kind cannot show that nothing of that kind is approved.
/// </summary>
public static class QualificationCriticalKinds
{
    public const string Attack = QualificationCaseKinds.Attack;

    public const string ImpostorPair = QualificationCaseKinds.ImpostorPair;

    public const string Altered = "altered";

    public const string Foreign = "foreign";

    public static IReadOnlyList<string> All { get; } = [Attack, ImpostorPair, Altered, Foreign];
}

public sealed record QualificationSplitCounts(
    string Split,
    int Persons,
    int Captures,
    int GenuineCaptures,
    int ImpostorPairs,
    int DistinctImpostorPairs,
    int AlteredDocuments,
    int ForeignDocuments,
    IReadOnlyDictionary<string, int> AttacksByClass,
    IReadOnlyDictionary<string, int>? CriticalCheckIns = null)
{
    public IReadOnlyList<string> CriticalKindsMissing()
    {
        if (Captures == 0)
            return [];

        return [.. QualificationCriticalKinds.All.Where(kind => CriticalCheckIns is null || CriticalCheckIns.GetValueOrDefault(kind) == 0)];
    }

    public string Describe()
    {
        var attacks = string.Join(", ", AttacksByClass.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}={pair.Value}")));
        var wholes = CriticalCheckIns;
        var critical = wholes is null
            ? string.Empty
            : " wholeCheckIns[" + string.Join(", ", QualificationCriticalKinds.All.Select(kind => string.Create(CultureInfo.InvariantCulture, $"{kind}={wholes.GetValueOrDefault(kind)}"))) + "]";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Split}: persons={Persons} captures={Captures} genuine={GenuineCaptures} impostorPairs={ImpostorPairs} (distinct {DistinctImpostorPairs}) altered={AlteredDocuments} foreign={ForeignDocuments} attacks[{attacks}]{critical}");
    }
}

public static class QualificationManifestWarningCodes
{
    public const string CriticalKindMissing = "critical_kind_missing";
}

/// <summary>One warning of a manifest: a code, the split it is about and the kind of critical case (a code of <see cref="QualificationCriticalKinds"/>). Never a value.</summary>
public sealed record QualificationManifestWarning(string Code, string Split, string Kind)
{
    public override string ToString()
    {
        return $"{Code} split={Split} kind={Kind}";
    }
}

public sealed record QualificationManifestReport(IReadOnlyList<QualificationManifestError> Errors, IReadOnlyList<QualificationSplitCounts> Splits)
{
    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<QualificationManifestWarning> Warnings =>
        [.. Splits.SelectMany(split => split.CriticalKindsMissing().Select(kind => new QualificationManifestWarning(QualificationManifestWarningCodes.CriticalKindMissing, split.Split, kind)))];

    public override string ToString()
    {
        var text = new StringBuilder(IsValid ? "valid" : string.Create(CultureInfo.InvariantCulture, $"{Errors.Count} error(s)"));
        foreach (var error in Errors)
            text.Append("\n  ").Append(error);

        foreach (var split in Splits)
            text.Append("\n  ").Append(split.Describe());

        foreach (var warning in Warnings)
            text.Append("\n  warning ").Append(warning);

        return text.ToString();
    }
}

public static class QualificationManifestValidator
{
    private const int MaxIdLength = 64;
    private const int MaxPathLength = 512;
    private const string ForbiddenPathCharacters = "<>:\"|?*";

    public static QualificationManifestReport Validate(QualificationDatasetManifest manifest, string folder)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return new Run(folder).Validate(manifest);
    }

    private static bool IsSafeId([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaxIdLength || !char.IsAsciiLetterOrDigit(value[0]))
            return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }

    private static string Position(string list, int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{list}[{index}]");
    }

    private sealed record SeenCapture(
        string? Id,
        string At,
        string? PersonId,
        string? Split,
        string? Session,
        string? Device,
        string? Kind,
        string? AttackClass,
        string? OwnerId,
        bool Altered,
        bool Foreign,
        bool WholeCheckIn);

    private sealed class Run
    {
        private readonly List<QualificationManifestError> _errors = [];
        private readonly HashSet<string> _persons = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _splitOfPerson = new(StringComparer.Ordinal);
        private readonly List<SeenCapture> _seen = [];
        private readonly string _root;
        private readonly bool _folderExists;

        public Run(string folder)
        {
            var full = Path.GetFullPath(folder);
            _folderExists = Directory.Exists(full);
            _root = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
        }

        public QualificationManifestReport Validate(QualificationDatasetManifest manifest)
        {
            if (!_folderExists)
                Add(QualificationManifestErrorCodes.FolderMissing);

            if (!IsSafeId(manifest.DatasetId))
                Add(QualificationManifestErrorCodes.DatasetIdInvalid, field: "datasetId");

            if (!IsSafeId(manifest.Version))
                Add(QualificationManifestErrorCodes.VersionInvalid, field: "version");

            CheckRights(manifest.Rights);
            CheckPersons(manifest.Persons);
            CheckCaptures(manifest.Captures);
            if (manifest.Generalization)
                CheckGeneralization();

            return new QualificationManifestReport(_errors, CountPerSplit());
        }

        private void CheckRights(QualificationRights? rights)
        {
            if (rights is null)
            {
                Add(QualificationManifestErrorCodes.RightsMissing, field: "rights");
                return;
            }

            if (string.IsNullOrWhiteSpace(rights.Purpose))
                Add(QualificationManifestErrorCodes.RightsPurposeMissing, field: "rights.purpose");

            if (string.IsNullOrWhiteSpace(rights.ConsentReference))
                Add(QualificationManifestErrorCodes.RightsConsentReferenceMissing, field: "rights.consentReference");

            if (string.IsNullOrWhiteSpace(rights.CollectedBy))
                Add(QualificationManifestErrorCodes.RightsCollectedByMissing, field: "rights.collectedBy");

            if (rights.TrainingUse != false)
                Add(QualificationManifestErrorCodes.RightsTrainingUseNotFalse, field: "rights.trainingUse");
        }

        private void CheckPersons(IReadOnlyList<QualificationPerson>? persons)
        {
            if (persons is null || persons.Count == 0)
            {
                Add(QualificationManifestErrorCodes.PersonsEmpty, field: "persons");
                return;
            }

            var listed = new Dictionary<string, int>(StringComparer.Ordinal);
            var splitsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (var index = 0; index < persons.Count; index++)
            {
                var person = persons[index];
                if (person is null || !IsSafeId(person.Id))
                {
                    Add(QualificationManifestErrorCodes.PersonIdInvalid, field: Position("persons", index));
                    continue;
                }

                _persons.Add(person.Id);
                listed[person.Id] = listed.GetValueOrDefault(person.Id) + 1;
                if (!splitsOf.TryGetValue(person.Id, out var splits))
                {
                    splits = new HashSet<string>(StringComparer.Ordinal);
                    splitsOf[person.Id] = splits;
                }

                if (person.Split is not null && QualificationSplits.All.Contains(person.Split))
                    splits.Add(person.Split);
                else
                    Add(QualificationManifestErrorCodes.PersonSplitUnknown, personId: person.Id, field: "split");
            }

            foreach (var (id, times) in listed)
            {
                var splits = splitsOf[id];
                if (splits.Count > 1)
                    Add(QualificationManifestErrorCodes.PersonInTwoSplits, personId: id);
                else if (times > 1)
                    Add(QualificationManifestErrorCodes.PersonIdDuplicate, personId: id);

                if (splits.Count == 1)
                    _splitOfPerson[id] = splits.Single();
            }
        }

        private void CheckCaptures(IReadOnlyList<QualificationCapture>? captures)
        {
            if (captures is null || captures.Count == 0)
            {
                Add(QualificationManifestErrorCodes.CapturesEmpty, field: "captures");
                return;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < captures.Count; index++)
            {
                var capture = captures[index];
                if (capture is null || !IsSafeId(capture.Id))
                    Add(QualificationManifestErrorCodes.CaptureIdInvalid, field: Position("captures", index));
                else if (!ids.Add(capture.Id) && duplicates.Add(capture.Id))
                    Add(QualificationManifestErrorCodes.CaptureIdDuplicate, captureId: capture.Id);

                if (capture is not null)
                    CheckCapture(capture, index);
            }
        }

        private void CheckCapture(QualificationCapture capture, int index)
        {
            var id = IsSafeId(capture.Id) ? capture.Id : null;
            var at = id is null ? Position("captures", index) + "." : string.Empty;

            var personId = IsSafeId(capture.Person) ? capture.Person : null;
            var known = personId is not null && _persons.Contains(personId);
            if (!known)
                Add(QualificationManifestErrorCodes.CapturePersonUnknown, captureId: id, field: at + "person");

            var split = known ? _splitOfPerson.GetValueOrDefault(personId!) : null;

            var session = IsSafeId(capture.Session) ? capture.Session : null;
            if (session is null)
                Add(QualificationManifestErrorCodes.CaptureSessionInvalid, captureId: id, field: at + "session");

            var device = IsSafeId(capture.Device) ? capture.Device : null;
            if (device is null)
                Add(QualificationManifestErrorCodes.CaptureDeviceInvalid, captureId: id, field: at + "device");

            if (capture.Channel is null || !IdentityChannels.All.Contains(capture.Channel))
                Add(QualificationManifestErrorCodes.CaptureChannelUnknown, captureId: id, field: at + "channel");

            var kind = capture.Kind is not null && QualificationCaptureKinds.All.Contains(capture.Kind) ? capture.Kind : null;
            if (kind is null)
                Add(QualificationManifestErrorCodes.CaptureKindUnknown, captureId: id, field: at + "kind");

            var attackClass = CheckAttackClass(capture, kind, id, at);

            if (capture.Document is null && capture.Sequence is null)
                Add(QualificationManifestErrorCodes.CaptureEmpty, captureId: id, field: at + "document");

            string? ownerId = null;
            if (capture.Document is { } document)
                ownerId = CheckDocument(document, personId, id, at);

            if (capture.Sequence is { } sequence)
                CheckSequence(sequence, id, at);

            _seen.Add(new SeenCapture(
                id,
                at,
                known ? personId : null,
                split,
                session,
                device,
                kind,
                attackClass,
                ownerId,
                capture.Document?.Altered ?? false,
                capture.Document?.Foreign ?? false,
                capture.Document is not null && capture.Sequence is not null));
        }

        private string? CheckAttackClass(QualificationCapture capture, string? kind, string? id, string at)
        {
            var given = !string.IsNullOrWhiteSpace(capture.AttackClass);
            if (given && !QualificationAttackClasses.All.Contains(capture.AttackClass!))
            {
                Add(QualificationManifestErrorCodes.AttackClassUnknown, captureId: id, field: at + "attackClass");
                return null;
            }

            if (kind == QualificationCaptureKinds.Attack && !given)
                Add(QualificationManifestErrorCodes.AttackClassMissing, captureId: id, field: at + "attackClass");
            else if (kind == QualificationCaptureKinds.Genuine && given)
                Add(QualificationManifestErrorCodes.AttackClassOnGenuine, captureId: id, field: at + "attackClass");

            return given ? capture.AttackClass : null;
        }

        private string? CheckDocument(QualificationDocument document, string? personId, string? id, string at)
        {
            if (!IsSafeId(document.Format))
                Add(QualificationManifestErrorCodes.DocumentFormatInvalid, captureId: id, field: at + "document.format");

            CheckFile(document.Front, directory: false, required: true, id, at + "document.front");
            CheckFile(document.Back, directory: false, required: false, id, at + "document.back");
            CheckFile(document.GroundTruthFile, directory: false, required: true, id, at + "document.groundTruthFile");

            var ownerId = IsSafeId(document.OwnerPerson) ? document.OwnerPerson : null;
            if (ownerId is null || !_persons.Contains(ownerId))
            {
                Add(QualificationManifestErrorCodes.DocumentOwnerUnknown, captureId: id, field: at + "document.ownerPerson");
                return null;
            }

            if (personId is not null
                && _splitOfPerson.TryGetValue(personId, out var captureSplit)
                && _splitOfPerson.TryGetValue(ownerId, out var ownerSplit)
                && captureSplit != ownerSplit)
            {
                Add(QualificationManifestErrorCodes.DocumentOwnerSplitMismatch, personId: ownerId, captureId: id, field: at + "document.ownerPerson");
            }

            return ownerId;
        }

        private void CheckSequence(QualificationSequence sequence, string? id, string at)
        {
            CheckFile(sequence.Dir, directory: true, required: true, id, at + "sequence.dir");
            CheckFile(sequence.TimestampsFile, directory: false, required: true, id, at + "sequence.timestampsFile");

            var challenge = sequence.Challenge;
            if (challenge is null || challenge.Count == 0 || challenge.Any(action => action is null || !PresenceActions.All.Contains(action)))
                Add(QualificationManifestErrorCodes.SequenceChallengeInvalid, captureId: id, field: at + "sequence.challenge");
        }

        private void CheckFile(string? relative, bool directory, bool required, string? id, string field)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                if (required)
                    Add(QualificationManifestErrorCodes.PathMissing, captureId: id, field: field);

                return;
            }

            if (!TryResolve(relative, out var full))
            {
                Add(QualificationManifestErrorCodes.PathInvalid, captureId: id, field: field);
                return;
            }

            if (_folderExists && !(directory ? Directory.Exists(full) : File.Exists(full)))
                Add(QualificationManifestErrorCodes.FileMissing, captureId: id, field: field);
        }

        private bool TryResolve(string relative, out string full)
        {
            full = string.Empty;
            if (relative.Length > MaxPathLength)
                return false;

            foreach (var character in relative)
            {
                if (char.IsControl(character) || ForbiddenPathCharacters.Contains(character, StringComparison.Ordinal))
                    return false;
            }

            var normalized = relative.Replace('\\', '/');
            if (normalized.StartsWith('/'))
                return false;

            foreach (var segment in normalized.Split('/'))
            {
                if (segment.Length == 0 || segment[^1] is '.' or ' ')
                    return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(_root, normalized));
            if (!candidate.StartsWith(_root, StringComparison.Ordinal))
                return false;

            full = candidate;
            return true;
        }

        private void CheckGeneralization()
        {
            CheckDisjoint(capture => capture.Session, QualificationManifestErrorCodes.SessionSharedAcrossSplits, "session");
            CheckDisjoint(capture => capture.Device, QualificationManifestErrorCodes.DeviceSharedAcrossSplits, "device");
        }

        private void CheckDisjoint(Func<SeenCapture, string?> key, string code, string field)
        {
            var splitsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var capture in _seen)
            {
                if (key(capture) is not { } value || capture.Split is not { } split)
                    continue;

                if (!splitsOf.TryGetValue(value, out var splits))
                {
                    splits = new HashSet<string>(StringComparer.Ordinal);
                    splitsOf[value] = splits;
                }

                splits.Add(split);
            }

            foreach (var capture in _seen)
            {
                if (key(capture) is { } value && capture.Split is not null && splitsOf[value].Count > 1)
                    Add(code, captureId: capture.Id, field: capture.At + field);
            }
        }

        private List<QualificationSplitCounts> CountPerSplit()
        {
            var counts = new List<QualificationSplitCounts>();
            foreach (var split in QualificationSplits.All)
            {
                var captures = _seen.Where(capture => capture.Split == split).ToList();
                var impostors = captures
                    .Where(capture => capture.Kind == QualificationCaptureKinds.Genuine && capture.PersonId is not null && capture.OwnerId is not null && capture.OwnerId != capture.PersonId)
                    .ToList();
                var attacks = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var attackClass in QualificationAttackClasses.All)
                    attacks[attackClass] = captures.Count(capture => capture.Kind == QualificationCaptureKinds.Attack && capture.AttackClass == attackClass);

                var wholes = captures.Where(capture => capture.WholeCheckIn).ToList();
                var critical = new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [QualificationCriticalKinds.Attack] = wholes.Count(capture => capture.Kind == QualificationCaptureKinds.Attack),
                    [QualificationCriticalKinds.ImpostorPair] = impostors.Count(capture => capture.WholeCheckIn),
                    [QualificationCriticalKinds.Altered] = wholes.Count(capture => capture.Altered),
                    [QualificationCriticalKinds.Foreign] = wholes.Count(capture => capture.Foreign),
                };

                counts.Add(new QualificationSplitCounts(
                    split,
                    _splitOfPerson.Count(person => person.Value == split),
                    captures.Count,
                    captures.Count(capture => capture.Kind == QualificationCaptureKinds.Genuine),
                    impostors.Count,
                    impostors.Select(capture => (capture.PersonId, capture.OwnerId)).Distinct().Count(),
                    captures.Count(capture => capture.Altered),
                    captures.Count(capture => capture.Foreign),
                    attacks,
                    critical));
            }

            return counts;
        }

        private void Add(string code, string? personId = null, string? captureId = null, string? field = null)
        {
            _errors.Add(new QualificationManifestError(code, personId, captureId, field));
        }
    }
}
