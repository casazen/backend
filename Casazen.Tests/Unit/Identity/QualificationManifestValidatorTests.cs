using System.Reflection;
using System.Text.Json;
using Casazen.Core.Identity;
using Casazen.Tests.Qualification;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E74/F01/US01/T02: the dataset manifest of the qualification toolkit and its validator, on manifests written to a temp folder. The folder is built in code from
/// placeholder text files: no photo, no frame and no document image is committed (the repository is public), and the ground-truth files hold only a sentinel
/// that no error, report or message may ever repeat. The two proofs of the brief (data): <c>QualificationManifestValidator_PersonInTwoSplits_IsRefused</c>
/// (separation by person) and <c>QualificationManifestValidator_MissingRightsOrTrainingUse_IsRefused</c> (rights for the purpose).
/// </summary>
public sealed class QualificationManifestValidatorTests : IDisposable
{
    private const string GroundTruthSentinel = "GROUND-TRUTH-SENTINEL-4F81C2";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "casazen-qualification-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileStream> _held = [];

    public QualificationManifestValidatorTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        foreach (var held in _held)
            held.Dispose();

        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void QualificationManifestValidator_ValidManifest_HasNoErrors_AndReportsTheCountsPerSplitAndClass()
    {
        var manifest = ValidManifest();
        var path = Path.Combine(_folder, "manifest.json");
        File.WriteAllText(path, manifest.ToJson());

        var loaded = QualificationDatasetManifest.Load(path);
        var report = QualificationManifestValidator.Validate(loaded, _folder);

        Assert.True(report.IsValid, report.ToString());
        Assert.Empty(report.Errors);
        Assert.Equal(manifest.ToJson(), loaded.ToJson());
        Assert.Equal([QualificationSplits.Development, QualificationSplits.Calibration, QualificationSplits.Test], report.Splits.Select(split => split.Split));

        var development = report.Splits[0];
        Assert.Equal((2, 2, 2, 1, 1, 0, 0), Numbers(development));
        Assert.All(QualificationAttackClasses.All, attackClass => Assert.Equal(0, development.AttacksByClass[attackClass]));

        var calibration = report.Splits[1];
        Assert.Equal((1, 1, 1, 0, 0, 0, 0), Numbers(calibration));

        var test = report.Splits[2];
        Assert.Equal((2, 4, 2, 1, 1, 0, 0), Numbers(test));
        Assert.Equal(QualificationAttackClasses.All, test.AttacksByClass.Keys);
        Assert.Equal(1, test.AttacksByClass[QualificationAttackClasses.PhotoPrint]);
        Assert.Equal(1, test.AttacksByClass[QualificationAttackClasses.ScreenReplay]);
        Assert.Equal(0, test.AttacksByClass[QualificationAttackClasses.VideoReplay]);
    }

    [Fact]
    public void QualificationManifestValidator_PersonInTwoSplits_IsRefused()
    {
        var manifest = ValidManifest();
        var twice = manifest with { Persons = [.. manifest.Persons!, new QualificationPerson("p-dev-1", QualificationSplits.Test)] };

        var report = QualificationManifestValidator.Validate(RoundTrip(twice), _folder);

        Assert.False(report.IsValid);
        var error = Assert.Single(report.Errors);
        Assert.Equal(QualificationManifestErrorCodes.PersonInTwoSplits, error.Code);
        Assert.Equal("p-dev-1", error.PersonId);
        Assert.Null(error.CaptureId);
    }

    [Fact]
    public void QualificationManifestValidator_MissingRightsOrTrainingUse_IsRefused()
    {
        var manifest = ValidManifest();
        var rights = manifest.Rights!;

        // No rights at all: not a dataset to qualify on.
        Assert.Equal([QualificationManifestErrorCodes.RightsMissing], CodesOf(manifest with { Rights = null }));

        // Training use declared true, or not declared: a dataset that may train a model is not kept apart from the service.
        Assert.Equal([QualificationManifestErrorCodes.RightsTrainingUseNotFalse], CodesOf(manifest with { Rights = rights with { TrainingUse = true } }));
        Assert.Equal([QualificationManifestErrorCodes.RightsTrainingUseNotFalse], CodesOf(manifest with { Rights = rights with { TrainingUse = null } }));

        // Rights without a purpose, a consent reference or a collector.
        Assert.Equal(
            [
                QualificationManifestErrorCodes.RightsPurposeMissing,
                QualificationManifestErrorCodes.RightsConsentReferenceMissing,
                QualificationManifestErrorCodes.RightsCollectedByMissing,
            ],
            CodesOf(manifest with { Rights = rights with { Purpose = " ", ConsentReference = null, CollectedBy = string.Empty } }));

        // The same, through the JSON a person writes: no "rights" member, and "trainingUse": true.
        var withoutRights = QualificationDatasetManifest.Parse(RemoveMember(manifest.ToJson(), "rights"));
        Assert.Equal([QualificationManifestErrorCodes.RightsMissing], QualificationManifestValidator.Validate(withoutRights, _folder).Errors.Select(error => error.Code));

        var training = QualificationDatasetManifest.Parse(manifest.ToJson().Replace("\"trainingUse\": false", "\"trainingUse\": true", StringComparison.Ordinal));
        Assert.Equal([QualificationManifestErrorCodes.RightsTrainingUseNotFalse], QualificationManifestValidator.Validate(training, _folder).Errors.Select(error => error.Code));
    }

    [Fact]
    public void QualificationManifestValidator_OverlappingDevicesAndSessions_AreRefusedOnlyWhenGeneralizationIsEvaluated()
    {
        var manifest = ValidManifest();

        var sharedDevice = WithCapture(manifest, "c-test-1", capture => capture with { Device = "d-dev" });
        var deviceReport = QualificationManifestValidator.Validate(RoundTrip(sharedDevice), _folder);
        Assert.Equal(
            [
                (QualificationManifestErrorCodes.DeviceSharedAcrossSplits, "c-dev-1"),
                (QualificationManifestErrorCodes.DeviceSharedAcrossSplits, "c-dev-2"),
                (QualificationManifestErrorCodes.DeviceSharedAcrossSplits, "c-test-1"),
            ],
            deviceReport.Errors.Select(error => (error.Code, error.CaptureId)));
        Assert.All(deviceReport.Errors, error => Assert.Equal("device", error.Field));

        var sharedSession = WithCapture(manifest, "c-cal-1", capture => capture with { Session = "s-test" });
        var sessionReport = QualificationManifestValidator.Validate(RoundTrip(sharedSession), _folder);
        Assert.Equal(
            ["c-cal-1", "c-test-1", "c-test-2", "c-test-3", "c-test-4"],
            sessionReport.Errors.Select(error => error.CaptureId));
        Assert.All(sessionReport.Errors, error => Assert.Equal((QualificationManifestErrorCodes.SessionSharedAcrossSplits, "session"), (error.Code, error.Field)));

        // Without generalization only the persons must be separated: the same overlaps are accepted.
        Assert.True(QualificationManifestValidator.Validate(RoundTrip(sharedDevice with { Generalization = false }), _folder).IsValid);
        Assert.True(QualificationManifestValidator.Validate(RoundTrip(sharedSession with { Generalization = false }), _folder).IsValid);
    }

    [Fact]
    public void QualificationManifestValidator_UnknownAttackClass_IsRefused()
    {
        var manifest = ValidManifest();

        var unknown = QualificationManifestValidator.Validate(RoundTrip(WithCapture(manifest, "c-test-3", capture => capture with { AttackClass = "laser_projection" })), _folder);
        var error = Assert.Single(unknown.Errors);
        Assert.Equal((QualificationManifestErrorCodes.AttackClassUnknown, "c-test-3", "attackClass"), (error.Code, error.CaptureId, error.Field));
        Assert.DoesNotContain("laser_projection", unknown.ToString(), StringComparison.Ordinal);

        // An unknown class is never counted, under any name.
        Assert.Equal(0, unknown.Splits[2].AttacksByClass[QualificationAttackClasses.PhotoPrint]);
        Assert.Equal(QualificationAttackClasses.All, unknown.Splits[2].AttacksByClass.Keys);

        // An attack without a class, and a genuine capture with one.
        var missing = QualificationManifestValidator.Validate(RoundTrip(WithCapture(manifest, "c-test-3", capture => capture with { AttackClass = null })), _folder);
        Assert.Equal([(QualificationManifestErrorCodes.AttackClassMissing, "c-test-3")], missing.Errors.Select(item => (item.Code, item.CaptureId)));

        var onGenuine = QualificationManifestValidator.Validate(
            RoundTrip(WithCapture(manifest, "c-test-1", capture => capture with { AttackClass = QualificationAttackClasses.Mask })),
            _folder);
        Assert.Equal([(QualificationManifestErrorCodes.AttackClassOnGenuine, "c-test-1")], onGenuine.Errors.Select(item => (item.Code, item.CaptureId)));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("..\\outside.bin")]
    [InlineData("docs/../../outside.bin")]
    [InlineData("docs\\..\\..\\outside.bin")]
    [InlineData("docs/..")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\front.bin")]
    [InlineData("C:\\Windows\\front.bin")]
    [InlineData("C:/Windows/front.bin")]
    [InlineData("c:front.bin")]
    [InlineData("docs//c-dev-1-front.bin")]
    [InlineData("docs/c-dev-1-front.bin/")]
    [InlineData("docs/c-dev-1-front.bin ")]
    [InlineData("docs/c-dev-1-front.bin.")]
    [InlineData("docs/c-dev-*-front.bin")]
    public void QualificationManifestValidator_PathEscapingTheFolderOrNotPlainRelative_IsRefused(string front)
    {
        AssertFrontIsRefused(front);
    }

    [Fact]
    public void QualificationManifestValidator_ControlCharactersInAPath_AreRefused()
    {
        // Built here, not written as test data: a control character does not belong in the name of a test.
        foreach (var control in new[] { '\0', '\n', '\r', '\t', '\u001f', '\u007f' })
            AssertFrontIsRefused("docs/c-dev-1" + control + "-front.bin");
    }

    [Fact]
    public void QualificationManifestValidator_AFileOutsideTheFolder_IsNotAcceptedEvenWhenItExists()
    {
        // A real file one level up: it exists, and the path to it still goes outside the folder.
        var outside = "casazen-outside-" + Guid.NewGuid().ToString("N") + ".bin";
        var outsidePath = Path.Combine(Path.GetDirectoryName(_folder)!, outside);
        File.WriteAllText(outsidePath, "placeholder");
        try
        {
            var escaping = WithCapture(ValidManifest(), "c-dev-1", capture => capture with { Document = capture.Document! with { Front = "../" + outside } });

            var report = QualificationManifestValidator.Validate(escaping, _folder);

            Assert.Equal([QualificationManifestErrorCodes.PathInvalid], report.Errors.Select(error => error.Code));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void QualificationManifestValidator_MissingFileOrFolder_IsRefusedWithTheCaptureAndTheMember()
    {
        var manifest = ValidManifest();
        File.Delete(Path.Combine(_folder, "docs", "c-dev-1-back.bin"));
        File.Delete(Path.Combine(_folder, "truth", "c-dev-2.json"));
        Directory.Delete(Path.Combine(_folder, "seq", "c-cal-1"), recursive: true);
        File.Delete(Path.Combine(_folder, "seq", "c-test-1", "timestamps.json"));

        var report = QualificationManifestValidator.Validate(RoundTrip(manifest), _folder);

        Assert.Equal(
            [
                ("c-dev-1", "document.back"),
                ("c-dev-2", "document.groundTruthFile"),
                ("c-cal-1", "sequence.dir"),
                ("c-cal-1", "sequence.timestampsFile"),
                ("c-test-1", "sequence.timestampsFile"),
            ],
            report.Errors.Select(error => (error.CaptureId!, error.Field!)));
        Assert.All(report.Errors, error => Assert.Equal(QualificationManifestErrorCodes.FileMissing, error.Code));

        // A path that must be there and is not given.
        var withoutPath = QualificationManifestValidator.Validate(
            WithCapture(manifest, "c-test-2", capture => capture with { Document = capture.Document! with { Front = null, GroundTruthFile = " " } }),
            _folder);
        Assert.Equal(
            [(QualificationManifestErrorCodes.PathMissing, "document.front"), (QualificationManifestErrorCodes.PathMissing, "document.groundTruthFile")],
            withoutPath.Errors.Where(error => error.CaptureId == "c-test-2").Select(error => (error.Code, error.Field!)));
    }

    [Fact]
    public void QualificationManifestValidator_AFolderThatDoesNotExist_IsReportedOnce_NotOncePerFile()
    {
        var report = QualificationManifestValidator.Validate(ValidManifest(), Path.Combine(_folder, "not-there"));

        Assert.Equal([QualificationManifestErrorCodes.FolderMissing], report.Errors.Select(error => error.Code));
    }

    [Fact]
    public void QualificationManifestValidator_Errors_NeverCarryAGroundTruthValueOrAnythingTheAuthorTyped()
    {
        // Every free member that is refused holds a recognizable text; none of it, and none of the ground truth, may come back in any error, report or JSON of them.
        var manifest = ValidManifest() with { DatasetId = "SECRET DATASET TEXT", Version = "SECRET VERSION TEXT" };
        manifest = manifest with { Persons = [.. manifest.Persons!, new QualificationPerson("SECRET PERSON TEXT", "SECRET-SPLIT-TEXT")] };
        manifest = WithCapture(manifest, "c-dev-1", capture => capture with
        {
            Channel = "SECRET-CHANNEL-TEXT",
            Kind = "SECRET-KIND-TEXT",
            Session = "SECRET SESSION TEXT",
            Device = "SECRET DEVICE TEXT",
            Document = capture.Document! with { Format = "SECRET FORMAT TEXT", Front = "../SECRET-PATH-TEXT.bin", Back = "docs/SECRET-MISSING-FILE-TEXT.bin" },
            Sequence = capture.Sequence! with { Challenge = ["SECRET-ACTION-TEXT"] },
        });
        manifest = WithCapture(manifest, "c-test-3", capture => capture with { AttackClass = "SECRET-CLASS-TEXT" });

        // References to persons nobody lists are not echoed either, whatever they look like.
        manifest = WithCapture(
            manifest,
            "c-test-4",
            capture => capture with { Person = "SECRET-PERSON-REF-TEXT", Document = capture.Document! with { OwnerPerson = "SECRET-OWNER-REF-TEXT" } });

        var report = QualificationManifestValidator.Validate(manifest, _folder);

        Assert.False(report.IsValid);
        var everything = report + "\n" + JsonSerializer.Serialize(report) + "\n" + string.Join("\n", report.Errors.Select(error => error.ToString()));
        Assert.DoesNotContain("SECRET", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(GroundTruthSentinel, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("seq/", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(_folder, everything, StringComparison.Ordinal);
    }

    [Fact]
    public void QualificationManifestValidator_GroundTruthFiles_AreNeverOpened()
    {
        // Every ground-truth file is held open exclusively: reading one anywhere would fail with an IOException, and the validator only asks whether it exists.
        var manifest = ValidManifest();
        foreach (var path in Directory.GetFiles(Path.Combine(_folder, "truth")))
            _held.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));

        Assert.NotEmpty(_held);
        Assert.Throws<IOException>(() => File.ReadAllText(Path.Combine(_folder, "truth", "c-dev-1.json")));

        var report = QualificationManifestValidator.Validate(manifest, _folder);

        Assert.True(report.IsValid, report.ToString());
    }

    [Fact]
    public void QualificationManifestValidator_PersonsListedTwiceOrWithoutAValidIdOrSplit_AreRefused()
    {
        var manifest = ValidManifest();
        var persons = manifest.Persons!.ToList();
        persons.Add(new QualificationPerson("p-test-2", QualificationSplits.Test));
        persons.Add(new QualificationPerson("p-new-1", "validation"));
        persons.Add(new QualificationPerson("p-new-2", null));
        persons.Add(new QualificationPerson("not a safe id", QualificationSplits.Test));
        persons.Add(new QualificationPerson(null, QualificationSplits.Test));
        persons.Add(null!);

        var report = QualificationManifestValidator.Validate(RoundTrip(manifest with { Persons = persons }), _folder);

        Assert.Equal(
            [
                (QualificationManifestErrorCodes.PersonSplitUnknown, "p-new-1", "split"),
                (QualificationManifestErrorCodes.PersonSplitUnknown, "p-new-2", "split"),
                (QualificationManifestErrorCodes.PersonIdInvalid, null, "persons[8]"),
                (QualificationManifestErrorCodes.PersonIdInvalid, null, "persons[9]"),
                (QualificationManifestErrorCodes.PersonIdInvalid, null, "persons[10]"),
                (QualificationManifestErrorCodes.PersonIdDuplicate, "p-test-2", null),
            ],
            report.Errors.Select(error => (error.Code, error.PersonId, error.Field)));
    }

    [Fact]
    public void QualificationManifestValidator_CapturesWithBadReferences_AreRefusedWithTheirIds()
    {
        var manifest = ValidManifest();
        manifest = WithCapture(manifest, "c-dev-1", capture => capture with { Person = "p-ghost-1", Session = "bad session", Device = null, Channel = "web:netscape", Kind = "maybe" });
        manifest = WithCapture(manifest, "c-dev-2", capture => capture with { Document = null, Sequence = null });
        manifest = WithCapture(
            manifest,
            "c-cal-1",
            capture => capture with
            {
                Document = capture.Document! with { Format = "it cie", OwnerPerson = "p-ghost-2" },
                Sequence = capture.Sequence! with { Challenge = [PresenceActions.TurnLeft, "jump"] },
            });
        manifest = manifest with { Captures = [.. manifest.Captures!, manifest.Captures![3], manifest.Captures![3] with { Id = "bad id" }, null!] };

        var report = QualificationManifestValidator.Validate(RoundTrip(manifest), _folder);

        var errors = report.Errors.Select(error => (error.Code, error.CaptureId, error.PersonId, error.Field)).ToList();
        Assert.Equal(
            [
                (QualificationManifestErrorCodes.CapturePersonUnknown, "c-dev-1", null, "person"),
                (QualificationManifestErrorCodes.CaptureSessionInvalid, "c-dev-1", null, "session"),
                (QualificationManifestErrorCodes.CaptureDeviceInvalid, "c-dev-1", null, "device"),
                (QualificationManifestErrorCodes.CaptureChannelUnknown, "c-dev-1", null, "channel"),
                (QualificationManifestErrorCodes.CaptureKindUnknown, "c-dev-1", null, "kind"),
                (QualificationManifestErrorCodes.CaptureEmpty, "c-dev-2", null, "document"),
                (QualificationManifestErrorCodes.DocumentFormatInvalid, "c-cal-1", null, "document.format"),
                (QualificationManifestErrorCodes.DocumentOwnerUnknown, "c-cal-1", null, "document.ownerPerson"),
                (QualificationManifestErrorCodes.SequenceChallengeInvalid, "c-cal-1", null, "sequence.challenge"),
                (QualificationManifestErrorCodes.CaptureIdDuplicate, "c-test-1", null, null),
                (QualificationManifestErrorCodes.CaptureIdInvalid, null, null, "captures[8]"),
                (QualificationManifestErrorCodes.CaptureIdInvalid, null, null, "captures[9]"),
            ],
            errors);
    }

    [Fact]
    public void QualificationManifestValidator_DocumentOwner_MustBeAKnownPersonOfTheSameSplit()
    {
        var manifest = ValidManifest();

        // The document of a development person shown by a test person: the impostor pair would carry a development person into the test split.
        var crossing = WithCapture(manifest, "c-test-2", capture => capture with { Document = capture.Document! with { OwnerPerson = "p-dev-1" } });
        var report = QualificationManifestValidator.Validate(RoundTrip(crossing), _folder);

        var error = Assert.Single(report.Errors);
        Assert.Equal((QualificationManifestErrorCodes.DocumentOwnerSplitMismatch, "c-test-2", "p-dev-1", "document.ownerPerson"), (error.Code, error.CaptureId, error.PersonId, error.Field));

        // An owner nobody lists cannot give an impostor pair at all.
        var unknown = WithCapture(manifest, "c-test-2", capture => capture with { Document = capture.Document! with { OwnerPerson = null } });
        var unknownReport = QualificationManifestValidator.Validate(RoundTrip(unknown), _folder);
        Assert.Equal([(QualificationManifestErrorCodes.DocumentOwnerUnknown, "c-test-2")], unknownReport.Errors.Select(item => (item.Code, item.CaptureId)));
        Assert.Equal(0, unknownReport.Splits[2].ImpostorPairs);
    }

    [Fact]
    public void QualificationManifestValidator_ImpostorPairs_AreDerivedFromTheOwnerOfTheDocument()
    {
        var manifest = ValidManifest();
        var captures = manifest.Captures!.ToList();

        // Another capture of the same couple (a repeat), the opposite couple, an altered and a foreign document, and an attack with a document of somebody else.
        captures.Add(NewCapture("c-test-5", "p-test-2", "s-test", "d-test", owner: "p-test-1"));
        captures.Add(NewCapture("c-test-6", "p-test-1", "s-test", "d-test", owner: "p-test-2"));
        captures.Add(WithDocument(NewCapture("c-test-7", "p-test-1", "s-test", "d-test"), altered: true, foreign: false));
        captures.Add(WithDocument(NewCapture("c-test-8", "p-test-2", "s-test", "d-test"), altered: false, foreign: true));
        captures.Add(NewCapture("c-test-9", "p-test-1", "s-test", "d-test", kind: QualificationCaptureKinds.Attack, attackClass: QualificationAttackClasses.VideoReplay, owner: "p-test-2"));

        var report = QualificationManifestValidator.Validate(RoundTrip(manifest with { Captures = captures }), _folder);

        Assert.True(report.IsValid, report.ToString());
        var test = report.Splits[2];
        Assert.Equal(9, test.Captures);
        Assert.Equal(6, test.GenuineCaptures);
        Assert.Equal(3, test.ImpostorPairs);
        Assert.Equal(2, test.DistinctImpostorPairs);
        Assert.Equal(1, test.AlteredDocuments);
        Assert.Equal(1, test.ForeignDocuments);
        Assert.Equal(1, test.AttacksByClass[QualificationAttackClasses.VideoReplay]);

        // The other splits do not see them.
        Assert.Equal((1, 1), (report.Splits[0].ImpostorPairs, report.Splits[0].DistinctImpostorPairs));
        Assert.Equal(0, report.Splits[1].ImpostorPairs);
    }

    [Fact]
    public void QualificationManifestValidator_EmptyDataset_IsRefused()
    {
        var empty = new QualificationDatasetManifest("dataset-empty", "1", ValidManifest().Rights, false, [], null);

        var report = QualificationManifestValidator.Validate(empty, _folder);

        Assert.Equal([QualificationManifestErrorCodes.PersonsEmpty, QualificationManifestErrorCodes.CapturesEmpty], report.Errors.Select(error => error.Code));
        Assert.Equal(QualificationSplits.All, report.Splits.Select(split => split.Split));
        Assert.All(report.Splits, split => Assert.Equal((0, 0), (split.Persons, split.Captures)));
    }

    [Fact]
    public void QualificationManifestValidator_NullArguments_AreRefused()
    {
        var manifest = ValidManifest();

        Assert.Throws<ArgumentNullException>(() => QualificationManifestValidator.Validate(null!, _folder));
        Assert.Throws<ArgumentNullException>(() => QualificationManifestValidator.Validate(manifest, null!));
        Assert.Throws<ArgumentException>(() => QualificationManifestValidator.Validate(manifest, " "));
    }

    [Fact]
    public void QualificationManifestErrorCodes_AreSpelledAsTheContractFixesThem_AndEveryConstantIsInTheList()
    {
        Assert.Equal(
            [
                "manifest_folder_missing", "dataset_id_invalid", "version_invalid", "rights_missing", "rights_purpose_missing", "rights_consent_reference_missing",
                "rights_collected_by_missing", "rights_training_use_not_false", "persons_empty", "person_id_invalid", "person_id_duplicate", "person_in_two_splits",
                "person_split_unknown", "captures_empty", "capture_id_invalid", "capture_id_duplicate", "capture_person_unknown", "capture_session_invalid",
                "capture_device_invalid", "capture_channel_unknown", "capture_kind_unknown", "capture_empty", "attack_class_missing", "attack_class_unknown",
                "attack_class_on_genuine", "document_format_invalid", "document_owner_unknown", "document_owner_split_mismatch", "sequence_challenge_invalid",
                "path_missing", "path_invalid", "file_missing", "session_shared_across_splits", "device_shared_across_splits",
            ],
            QualificationManifestErrorCodes.All);
        Assert.Equal(StringConstants(typeof(QualificationManifestErrorCodes)).Order(), QualificationManifestErrorCodes.All.Order());
        Assert.Equal(QualificationManifestErrorCodes.All.Count, QualificationManifestErrorCodes.All.Distinct().Count());
        Assert.Equal(["development", "calibration", "test"], QualificationSplits.All);
        Assert.Equal(["genuine", "attack"], QualificationCaptureKinds.All);
        Assert.Equal(["photo_print", "screen_replay", "video_replay", "synthetic", "mask", "other"], QualificationAttackClasses.All);
    }

    [Fact]
    public void QualificationDatasetManifest_ReadsTheJsonOfTheBrief_WithEveryMember()
    {
        const string json = """
            {
              "datasetId": "dataset-brief-01",
              "version": "2026.11.1",
              "rights": { "purpose": "qualification of the identity check", "consentReference": "CONSENT-REF-7", "collectedBy": "qa-team", "trainingUse": false },
              "generalization": true,
              "persons": [ { "id": "p1", "split": "development" }, { "id": "p2", "split": "calibration" }, { "id": "p3", "split": "test" } ],
              "captures": [
                {
                  "id": "c1", "person": "p3", "session": "s1", "device": "d1", "channel": "web:android-chrome", "kind": "attack", "attackClass": "screen_replay",
                  "document": { "format": "it-cie-3", "front": "docs/c1-front.bin", "back": "docs/c1-back.bin", "ownerPerson": "p3", "altered": true, "foreign": false,
                                "groundTruthFile": "truth/c1.json" },
                  "sequence": { "dir": "seq/c1", "timestampsFile": "seq/c1/timestamps.json", "challenge": ["turn_left", "look_down"] }
                }
              ]
            }
            """;

        var manifest = QualificationDatasetManifest.Parse(json);

        Assert.Equal(("dataset-brief-01", "2026.11.1", true), (manifest.DatasetId, manifest.Version, manifest.Generalization));
        Assert.Equal(("qualification of the identity check", "CONSENT-REF-7", "qa-team", false), (manifest.Rights!.Purpose, manifest.Rights.ConsentReference, manifest.Rights.CollectedBy, manifest.Rights.TrainingUse));
        Assert.Equal(
            [("p1", "development"), ("p2", "calibration"), ("p3", "test")],
            manifest.Persons!.Select(person => (person.Id!, person.Split!)));

        var capture = Assert.Single(manifest.Captures!);
        Assert.Equal(("c1", "p3", "s1", "d1", "web:android-chrome", "attack", "screen_replay"), (capture.Id, capture.Person, capture.Session, capture.Device, capture.Channel, capture.Kind, capture.AttackClass));
        Assert.Equal(("it-cie-3", "docs/c1-front.bin", "docs/c1-back.bin", "p3", true, false, "truth/c1.json"), (capture.Document!.Format, capture.Document.Front, capture.Document.Back, capture.Document.OwnerPerson, capture.Document.Altered, capture.Document.Foreign, capture.Document.GroundTruthFile));
        Assert.Equal(("seq/c1", "seq/c1/timestamps.json"), (capture.Sequence!.Dir, capture.Sequence.TimestampsFile));
        Assert.Equal([PresenceActions.TurnLeft, PresenceActions.LookDown], capture.Sequence.Challenge);
        Assert.Equal(IdentityChannels.AndroidChrome, capture.Channel);
    }

    [Fact]
    public void QualificationDatasetManifest_AMemberItDoesNotKnowOrABrokenText_IsRefused_WithoutQuotingIt()
    {
        string[] texts =
        [
            """{ "datasetId": "SECRET-DATASET-TEXT", "rights": { "trainingUsee": false } }""",
            """{ "datasetId": "SECRET-DATASET-TEXT" "version": "1" }""",
            """{ "datasetId": "SECRET-DATASET-TEXT", "generalization": "SECRET-NOT-A-BOOL" }""",
            """{ "datasetId": "SECRET-DATASET-TEXT", "persons": [ { "id": "p1", "split": "test", "name": "SECRET-NAME-TEXT" } ] }""",

            // The NAME of a member it does not know is text the author typed too, and the path of the parser holds it.
            """{ "datasetId": "SECRET-DATASET-TEXT", "SECRET-MEMBER-NAME": 1 }""",
            """{ "persons": [ { "id": "p1", "split": "test", "SECRET-NAME-MEMBER": "x" } ] }""",
            """{ "rights": { "SECRET RIGHTS MEMBER": false } }""",
            "SECRET-NOT-JSON",
            "null",
            string.Empty,
        ];

        foreach (var text in texts)
        {
            var exception = Assert.Throws<QualificationManifestFormatException>(() => QualificationDatasetManifest.Parse(text));

            Assert.DoesNotContain("SECRET", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", exception.JsonPath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QualificationDatasetManifest_ARefusedText_NamesOnlyKnownMembersAndIndexes_AndTheLineCountsFromOne()
    {
        // (text, path, line): a member the manifest knows is written as the model writes it, whatever the letter case; any other name is "?"; an index is a number.
        (string Text, string Path, long? Line)[] cases =
        [
            ("""{ "datasetId": "d", "SECRET-NAME": 1 }""", "$.?", 1),
            ("{\n  \"datasetId\": \"d\",\n  \"persons\": [\n    { \"id\": \"p1\", \"SECRET-NAME\": 1 }\n  ]\n}", "$.persons[0].?", 4),
            ("""{ "SECRET NAME": 1 }""", "$.?", 1),
            ("""{ "persons": [ { "SECRET NAME": 1 } ] }""", "$.persons[0].?", 1),
            ("""{ "captures": [ {}, {}, { "document": { "front": 5 } } ] }""", "$.captures[2].document.front", 1),
            ("""{ "GENERALIZATION": "x" }""", "$.generalization", 1),
            ("""{ "persons": 5 }""", "$.persons", 1),
            ("{\n  \"datasetId\": \"d\"\n  \"persons\": []\n}", "$", 3),
        ];

        foreach (var (text, path, line) in cases)
        {
            var exception = Assert.Throws<QualificationManifestFormatException>(() => QualificationDatasetManifest.Parse(text));

            Assert.Equal((path, line), (exception.JsonPath, exception.Line));
            Assert.Equal($"The qualification manifest cannot be read (at {path}, line {line}).", exception.Message);
        }

        // A text that is not a manifest at all has no place to name.
        var nothing = Assert.Throws<QualificationManifestFormatException>(() => QualificationDatasetManifest.Parse("null"));
        Assert.Equal(("$", (long?)null, "The qualification manifest cannot be read (at $, line unknown)."), (nothing.JsonPath, nothing.Line, nothing.Message));
    }

    [Fact]
    public void QualificationDatasetManifest_WhateverTheAuthorTypedAsAMemberName_NeverComesBack()
    {
        // The parser writes a name with special characters in brackets and does not escape it, so a name can look like part of a path.
        string[] names =
        [
            "SECRET-NAME",
            "SECRET NAME",
            "SECRET.NAME.TEXT",
            "SECRET'NAME",
            "SECRET']NAME[7]",
            "SECRET\"]NAME",
            "SECRET\\NAME",
            "$SECRET",
            "[SECRET]",
            "SECRET'].persons[0].id",
            "persons[0].SECRET",
            "SECRET-" + (char)0xE9,
        ];

        foreach (var name in names)
        {
            foreach (var template in new[] { "{ {0}: 1 }", "{ \"persons\": [ { {0}: 1 } ] }", "{ \"rights\": { {0}: false } }" })
            {
                var text = template.Replace("{0}", JsonSerializer.Serialize(name), StringComparison.Ordinal);

                var exception = Assert.Throws<QualificationManifestFormatException>(() => QualificationDatasetManifest.Parse(text));

                Assert.DoesNotContain("SECRET", exception.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("SECRET", exception.JsonPath, StringComparison.Ordinal);

                // Only known members, "?" and indexes are ever written.
                Assert.Matches(@"^\$(\.[A-Za-z]+|\.\?|\[[0-9]+\])*$", exception.JsonPath);
            }
        }
    }

    [Fact]
    public void QualificationJsonPlace_SafePath_WritesOnlyKnownNamesQuestionMarksAndNumbers()
    {
        var known = QualificationJsonPlace.MemberNames(typeof(QualificationDatasetManifest), typeof(QualificationPerson));
        (string? Path, string Expected)[] cases =
        [
            (null, "$"),
            (string.Empty, "$"),
            ("$", "$"),
            ("$.persons[0].id", "$.persons[0].id"),
            ("$.PERSONS[007].Split", "$.persons[7].split"),
            ("$.persons[0].SECRET", "$.persons[0].?"),
            ("$.persons[0]['SECRET NAME']", "$.persons[0].?"),
            ("$['SECRET NAME']", "$.?"),
            ("$.persons[0]['SECRET']NAME[7]']", "$.persons[0].?[7]"),
            ("$.persons[0]['a']SECRET.id']", "$.persons[0].?.?"),
            ("$.persons[0]['SECRET", "$.persons[0].?"),
            ("$.persons[99999999999]", "$.persons"),
            ("$.persons[-1]", "$.persons"),
            ("$.persons[ 1 ]", "$.persons"),
            ("$.persons[0", "$.persons"),
            ("$.persons[", "$.persons"),
            ("SECRET", "$"),
            ("$.", "$.?"),
            ("$..", "$.?.?"),
        ];

        foreach (var (path, expected) in cases)
            Assert.Equal(expected, QualificationJsonPlace.SafePath(path, known));
    }

    [Fact]
    public void QualificationDatasetManifest_AMemberThatIsNotThere_ReadsAsNullOrFalse_AndTheValidatorSaysWhatIsMissing()
    {
        var manifest = QualificationDatasetManifest.Parse("{}");

        Assert.Null(manifest.DatasetId);
        Assert.Null(manifest.Rights);
        Assert.False(manifest.Generalization);
        Assert.Null(manifest.Persons);
        Assert.Null(manifest.Captures);

        var report = QualificationManifestValidator.Validate(manifest, _folder);
        Assert.Equal(
            [
                QualificationManifestErrorCodes.DatasetIdInvalid,
                QualificationManifestErrorCodes.VersionInvalid,
                QualificationManifestErrorCodes.RightsMissing,
                QualificationManifestErrorCodes.PersonsEmpty,
                QualificationManifestErrorCodes.CapturesEmpty,
            ],
            report.Errors.Select(error => error.Code));
    }

    /// <summary>
    /// Proof (a target that cannot be measured fails): the validator counts, per split, the whole check-ins (a document and a sequence) of each kind of critical case, and a split that has captures
    /// and none of a kind is a WARNING (the manifest stays valid: a development split may do without; the test split may not, and the final test is not started on it).
    /// </summary>
    [Fact]
    public void Validate_TheWholeCheckInsOfEachKindOfCriticalCase_AreCounted_AndAKindWithNoneIsAWarningNotAnError()
    {
        var report = QualificationManifestValidator.Validate(RoundTrip(ValidManifest()), _folder);

        Assert.True(report.IsValid, report.ToString());

        // Test: two attacks and a person with the document of another, all with a sequence; no altered and no foreign document.
        var test = report.Splits[2];
        Assert.Equal((2, 1, 0, 0), (test.CriticalCheckIns!["attack"], test.CriticalCheckIns["impostor_pair"], test.CriticalCheckIns["altered"], test.CriticalCheckIns["foreign"]));
        Assert.Equal(["altered", "foreign"], test.CriticalKindsMissing());

        // Development has a person with the document of another and nothing else; calibration has one genuine capture: every kind is missing.
        Assert.Equal(["attack", "altered", "foreign"], report.Splits[0].CriticalKindsMissing());
        Assert.Equal(QualificationCriticalKinds.All, report.Splits[1].CriticalKindsMissing());

        // The warnings name the code, the split and the kind, and nothing else.
        Assert.Equal(9, report.Warnings.Count);
        Assert.All(report.Warnings, warning => Assert.Equal(QualificationManifestWarningCodes.CriticalKindMissing, warning.Code));
        Assert.Contains(report.Warnings, warning => warning.Split == QualificationSplits.Test && warning.Kind == QualificationCriticalKinds.Altered);
        Assert.Contains("warning critical_kind_missing split=test kind=altered", report.ToString(), StringComparison.Ordinal);
        Assert.Contains("wholeCheckIns[attack=2, impostor_pair=1, altered=0, foreign=0]", test.Describe(), StringComparison.Ordinal);

        // A split with no capture is not asked for them.
        var empty = QualificationManifestValidator.Validate(RoundTrip(ValidManifest() with { Persons = [new QualificationPerson("p-dev-1", QualificationSplits.Development)], Captures = [NewCapture("c-dev-9", "p-dev-1", "s-dev", "d-dev")] }), _folder);
        Assert.Empty(empty.Splits[2].CriticalKindsMissing());
    }

    [Fact]
    public void Validate_ACaptureWithoutASequence_IsNoWholeCheckIn_OfAnyKindOfCriticalCase()
    {
        // c-test-3 (an attack) and c-test-2 (a person with the document of another, altered and foreign) have a document and no sequence: nothing can be decided on them.
        var manifest = WithCapture(
            WithCapture(ValidManifest(), "c-test-3", capture => capture with { Sequence = null }),
            "c-test-2",
            capture => WithDocument(capture, altered: true, foreign: true) with { Sequence = null });

        var test = QualificationManifestValidator.Validate(RoundTrip(manifest), _folder).Splits[2];

        Assert.Equal((1, 0, 0, 0), (test.CriticalCheckIns!["attack"], test.CriticalCheckIns["impostor_pair"], test.CriticalCheckIns["altered"], test.CriticalCheckIns["foreign"]));

        // The counts of the documents are as they were: they count the documents, not the check-ins.
        Assert.Equal((1, 1, 1), (test.ImpostorPairs, test.AlteredDocuments, test.ForeignDocuments));
        Assert.Equal(["impostor_pair", "altered", "foreign"], test.CriticalKindsMissing());
    }

    private void AssertFrontIsRefused(string front)
    {
        var escaping = WithCapture(ValidManifest(), "c-dev-1", capture => capture with { Document = capture.Document! with { Front = front } });

        var report = QualificationManifestValidator.Validate(escaping, _folder);

        var error = Assert.Single(report.Errors);
        Assert.Equal((QualificationManifestErrorCodes.PathInvalid, "c-dev-1", "document.front"), (error.Code, error.CaptureId, error.Field));
    }

    private static (int, int, int, int, int, int, int) Numbers(QualificationSplitCounts counts)
    {
        return (counts.Persons, counts.Captures, counts.GenuineCaptures, counts.ImpostorPairs, counts.DistinctImpostorPairs, counts.AlteredDocuments, counts.ForeignDocuments);
    }

    private List<string> CodesOf(QualificationDatasetManifest manifest)
    {
        return QualificationManifestValidator.Validate(RoundTrip(manifest), _folder).Errors.Select(error => error.Code).ToList();
    }

    // What a person would do: write the JSON, read it back.
    private static QualificationDatasetManifest RoundTrip(QualificationDatasetManifest manifest)
    {
        return QualificationDatasetManifest.Parse(manifest.ToJson());
    }

    private static string RemoveMember(string json, string member)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject().Where(property => property.Name != member))
                property.WriteTo(writer);

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static QualificationDatasetManifest WithCapture(
        QualificationDatasetManifest manifest,
        string id,
        Func<QualificationCapture, QualificationCapture> change)
    {
        return manifest with { Captures = manifest.Captures!.Select(capture => capture.Id == id ? change(capture) : capture).ToList() };
    }

    private static QualificationCapture WithDocument(QualificationCapture capture, bool altered, bool foreign)
    {
        return capture with { Document = capture.Document! with { Altered = altered, Foreign = foreign } };
    }

    // Seven captures in three splits; development and calibration and test each have their own sessions and devices, and one impostor pair in development and
    // in test (a person shows the document of another person of the same split).
    private QualificationDatasetManifest ValidManifest()
    {
        return new QualificationDatasetManifest(
            "dataset-synthetic-01",
            "1",
            new QualificationRights("qualification of the self-hosted identity verification", "CONSENT-REF-0001", "qa-team", false),
            true,
            [
                new QualificationPerson("p-dev-1", QualificationSplits.Development),
                new QualificationPerson("p-dev-2", QualificationSplits.Development),
                new QualificationPerson("p-cal-1", QualificationSplits.Calibration),
                new QualificationPerson("p-test-1", QualificationSplits.Test),
                new QualificationPerson("p-test-2", QualificationSplits.Test),
            ],
            [
                NewCapture("c-dev-1", "p-dev-1", "s-dev", "d-dev"),
                NewCapture("c-dev-2", "p-dev-2", "s-dev", "d-dev", owner: "p-dev-1"),
                NewCapture("c-cal-1", "p-cal-1", "s-cal", "d-cal"),
                NewCapture("c-test-1", "p-test-1", "s-test", "d-test"),
                NewCapture("c-test-2", "p-test-2", "s-test", "d-test", owner: "p-test-1"),
                NewCapture("c-test-3", "p-test-1", "s-test", "d-test", kind: QualificationCaptureKinds.Attack, attackClass: QualificationAttackClasses.PhotoPrint),
                NewCapture("c-test-4", "p-test-2", "s-test", "d-test", kind: QualificationCaptureKinds.Attack, attackClass: QualificationAttackClasses.ScreenReplay),
            ]);
    }

    // One capture and the placeholder files it points at, written under the temp folder (text, never an image).
    private QualificationCapture NewCapture(
        string id,
        string person,
        string session,
        string device,
        string kind = QualificationCaptureKinds.Genuine,
        string? attackClass = null,
        string? owner = null)
    {
        Write($"docs/{id}-front.bin", "placeholder");
        Write($"docs/{id}-back.bin", "placeholder");
        Write($"truth/{id}.json", GroundTruthSentinel + "-" + id);
        Write($"seq/{id}/frame-0001.bin", "placeholder");
        Write($"seq/{id}/timestamps.json", "[]");

        return new QualificationCapture(
            id,
            person,
            session,
            device,
            IdentityChannels.IosSafari,
            kind,
            attackClass,
            new QualificationDocument("it-cie-3", $"docs/{id}-front.bin", $"docs/{id}-back.bin", owner ?? person, false, false, $"truth/{id}.json"),
            new QualificationSequence($"seq/{id}", $"seq/{id}/timestamps.json", [PresenceActions.TurnLeft, PresenceActions.LookUp]));
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static IEnumerable<string> StringConstants(Type type)
    {
        return type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
    }
}
