using System.Globalization;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Qualification;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public sealed class BookingConsistencyDeclaredTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DocumentFormat Cie = IdentityTestDocuments.Cie();
    private static readonly IdentityDecisionPolicy Policy = IdentityTestDocuments.Policy();
    private static readonly DocumentCoherenceChecker Checker = new();
    private static readonly IdentityDecisionEngine Engine = new();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly DateOnly Day = new(2026, 12, 10);

    private static readonly VerificationReason NothingCompared = new(VerificationReasons.Checks.BookingConsistency, VerificationReasons.Results.NotApplicable);

    private static readonly CheckInIdentity NothingDeclared = new(null, " ", null, "", null);

    private static List<ZonedField> Card(string expiryDate = "2032-03-12", string surname = IdentityTestDocuments.Surname) =>
    [
        Printed(ZonedFieldNames.Surname, surname),
        Printed(ZonedFieldNames.GivenNames, IdentityTestDocuments.GivenNames),
        Printed(ZonedFieldNames.BirthDate, "1985-03-12"),
        Printed(ZonedFieldNames.DocumentNumber, IdentityTestDocuments.DocumentNumber),
        Printed(ZonedFieldNames.IssueDate, "2022-05-15"),
        Printed(ZonedFieldNames.ExpiryDate, expiryDate),
        Printed(ZonedFieldNames.Sex, "F"),
        Printed(ZonedFieldNames.Nationality, "ITA"),
    ];

    private static ZonedField Printed(string name, string value) => new(name, value, DocumentSide.Front, name, 0.55, ZonedFieldSources.Viz);

    private static MrzReading Reading(MrzExtractionResult? result = null) => new(result ?? IdentityTestDocuments.Mrz(), []);

    private static DocumentCoherenceReport Report(CheckInIdentity? declared, List<ZonedField>? card = null, MrzReading? mrz = null) =>
        Checker.Check(card ?? Card(), mrz ?? Reading(), new DocumentCoherenceContext(Day, declared), Cie, Policy);

    private static string Result(DocumentCoherenceReport report, string field) =>
        Assert.Single(report.Checks, entry => entry.Check == DocumentFormatChecks.BookingConsistency && entry.Field == field).Result;

    private static DateOnly? DayOf(string? text) => text is null ? null : DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static IdentityDecision Decide(IdentityDocumentCoherence coherence, SegmentResolution? segment) =>
        Engine.Decide(EvidenceBundles.Perfect(segment) with { DocumentCoherence = coherence });

    [Fact]
    public void NothingDeclared_ACoherentDocumentAMatchingFaceAndAPassedPresence_AreApprovedAutomatically_InAQualifiedSegment()
    {
        foreach (var declared in new[] { null, NothingDeclared })
        {
            var report = Report(declared);

            Assert.All(
                report.Checks.Where(entry => entry.Check == DocumentFormatChecks.BookingConsistency),
                entry => Assert.Equal(DocumentCoherenceResults.NotApplicable, entry.Result));
            Assert.Equal(DocumentCoherenceResults.NotApplicable, report.Summary(DocumentFormatChecks.BookingConsistency));
            Assert.Equal(0, report.UnknownCount);
            Assert.True(report.AllPassed(Cie.RequiredChecks));

            var coherence = IdentityDocumentCoherences.Of(report, Cie);
            Assert.Equal(IdentityDocumentCoherence.PassedWithoutBooking, coherence);

            var decision = Decide(coherence, EvidenceBundles.Allowed);
            Assert.Equal(IdentityFinalDecision.AutoApproved, decision.Final);
            Assert.Equal((IdentityShadowDecision.Approve, IdentityDecisionMode.Automatic, IdentityOutcomeCodes.AutoApproved), (decision.Shadow, decision.Mode, decision.OutcomeCode));
            Assert.Equal((BiometricOutcome.Accept, BiometricOutcome.Accept), (decision.LogOutcome, decision.ShadowOutcome));
            Assert.Single(decision.Reasons, reason => reason == NothingCompared);
            Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.Passed), decision.Reasons);
            Assert.All(decision.Reasons.Where(reason => reason != NothingCompared), reason => Assert.Equal(VerificationReasons.Results.Passed, reason.Result));
        }
    }

    [Theory]
    [InlineData("MARIA GIUSEPPINA", "ROSSI", null, null, null)]
    [InlineData(null, null, null, "CA12345AA", null)]
    [InlineData(null, null, "1985-03-12", null, "ITALIA")]
    [InlineData("Maria Giuseppina", "Rossi", "1985-03-12", "ca 12345 aa", "Italia")]
    public void ADeclaredValueEqualToTheDocument_Passes_AndAFieldWithNothingDeclaredIsNeutral(string? first, string? last, string? birth, string? number, string? citizenship)
    {
        var report = Report(new CheckInIdentity(first, last, DayOf(birth), number, citizenship));

        Assert.Equal(DocumentCoherenceResults.Passed, report.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.DoesNotContain(report.Checks, entry => entry.Result is DocumentCoherenceResults.Failed or DocumentCoherenceResults.Unknown);
        var coherence = IdentityDocumentCoherences.Of(report, Cie);
        Assert.Equal(IdentityDocumentCoherence.Passed, coherence);

        var decision = Decide(coherence, EvidenceBundles.Allowed);
        Assert.Equal(IdentityFinalDecision.AutoApproved, decision.Final);
        Assert.DoesNotContain(NothingCompared, decision.Reasons);
    }

    [Theory]
    [InlineData(DocumentCoherenceFields.Names, "GIOVANNI", "VERDI", null, null, null)]
    [InlineData(DocumentCoherenceFields.Names, null, "ROSSI", null, null, null)] // a lone surname is a difference, as MrzCheckInConsistency compares names
    [InlineData(ZonedFieldNames.BirthDate, null, null, "1990-01-01", null, null)]
    [InlineData(ZonedFieldNames.DocumentNumber, null, null, null, "CA99999ZZ", null)]
    [InlineData(ZonedFieldNames.Nationality, null, null, null, null, "FRANCIA")]
    [InlineData(ZonedFieldNames.DocumentNumber, "MARIA GIUSEPPINA", "ROSSI", "1985-03-12", "CA12345AB", "ITALIA")]
    public void ADeclaredValueThatDiffers_IsAKnownConflict_ReviewedWithItsCode_NeverApproved(
        string field, string? first, string? last, string? birth, string? number, string? citizenship)
    {
        var report = Report(new CheckInIdentity(first, last, DayOf(birth), number, citizenship));

        Assert.Equal(DocumentCoherenceResults.Failed, Result(report, field));
        Assert.Equal(DocumentCoherenceResults.Failed, report.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.False(report.AllPassed(Cie.RequiredChecks));

        Assert.False(report.IdentifyingConflict);
        Assert.Empty(report.Codes);

        var coherence = IdentityDocumentCoherences.Of(report, Cie);
        Assert.Equal(IdentityDocumentCoherence.BookingConflict, coherence);
        foreach (var segment in new SegmentResolution?[] { EvidenceBundles.Allowed, EvidenceBundles.Observation, null })
        {
            var decision = Decide(coherence, segment);

            Assert.Equal(IdentityFinalDecision.Review, decision.Final);
            Assert.Equal(IdentityShadowDecision.Review, decision.Shadow);
            Assert.Equal(IdentityOutcomeCodes.Review, decision.OutcomeCode);
            Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (decision.LogOutcome, decision.ShadowOutcome));
            Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.BookingMismatch), decision.Reasons);
            Assert.Empty(decision.Guidance);
        }
    }

    [Fact]
    public void ADeclaredValueThatCannotBeCompared_StaysUnknown_AndIsReviewed()
    {
        var demonym = Report(new CheckInIdentity("MARIA GIUSEPPINA", "ROSSI", null, null, "Italiana"));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(demonym, ZonedFieldNames.Nationality));
        Assert.Equal(DocumentCoherenceResults.Unknown, demonym.Summary(DocumentFormatChecks.BookingConsistency));
        Assert.Equal(IdentityDocumentCoherence.Unknown, IdentityDocumentCoherences.Of(demonym, Cie));

        var wrongDigit = Report(new CheckInIdentity(null, null, new DateOnly(1985, 3, 12), null, null), mrz: Reading(IdentityTestDocuments.Mrz(birthDigit: '9')));
        Assert.Equal(DocumentCoherenceResults.Unknown, Result(wrongDigit, ZonedFieldNames.BirthDate));

        foreach (var report in new[] { demonym, wrongDigit })
            Assert.NotEqual(IdentityFinalDecision.AutoApproved, Decide(IdentityDocumentCoherences.Of(report, Cie), EvidenceBundles.Allowed).Final);
    }

    [Fact]
    public void NothingDeclared_EveryOtherReasonOfTheDocument_StillSendsToAPerson()
    {
        var cases = new (string Name, DocumentCoherenceReport Report, IdentityDocumentCoherence Expected)[]
        {
            ("check digit", Report(NothingDeclared, mrz: Reading(IdentityTestDocuments.Mrz(birthDigit: '9'))), IdentityDocumentCoherence.MrzInvalid),
            ("expired", Report(NothingDeclared, Card(expiryDate: "2026-12-01"), Reading(IdentityTestDocuments.Mrz(expiry: "261201"))), IdentityDocumentCoherence.Expired),
            ("sides disagree", Report(NothingDeclared, Card(surname: "BIANCHI")), IdentityDocumentCoherence.IdentifyingConflict),
            ("a field read on one side only", Report(NothingDeclared, [Printed(ZonedFieldNames.Surname, IdentityTestDocuments.Surname)]), IdentityDocumentCoherence.Unknown),
            ("no MRZ read", Checker.Check(Card(), null, new DocumentCoherenceContext(Day, NothingDeclared), Cie, Policy), IdentityDocumentCoherence.Unknown),
        };

        foreach (var (name, report, expected) in cases)
        {
            var coherence = IdentityDocumentCoherences.Of(report, Cie);
            Assert.True(coherence == expected, $"{name}: {coherence}");

            var decision = Decide(coherence, EvidenceBundles.Allowed);
            Assert.True(decision.Final == IdentityFinalDecision.Review, $"{name}: {decision.Final}");
            Assert.True(decision.Shadow == IdentityShadowDecision.Review, $"{name}: shadow {decision.Shadow}");
        }

        var unread = cases[^1].Report;
        Assert.Equal(DocumentCoherenceResults.Unknown, unread.Summary(DocumentFormatChecks.BookingConsistency));

        var incomplete = Engine.Decide(EvidenceBundles.Perfect(EvidenceBundles.Allowed) with
        {
            DocumentCoherence = IdentityDocumentCoherences.Of(Report(NothingDeclared), Cie),
            DocumentComplete = false,
        });
        Assert.Equal((IdentityFinalDecision.Review, IdentityShadowDecision.Review), (incomplete.Final, incomplete.Shadow));
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.NotRun), incomplete.Reasons);
        Assert.DoesNotContain(NothingCompared, incomplete.Reasons);
    }

    [Theory]
    [InlineData(IdentitySegmentReasonCodes.PolicyProvisional)]
    [InlineData(IdentitySegmentReasonCodes.SegmentObservation)]
    [InlineData(IdentitySegmentReasonCodes.SegmentNotQualified)]
    [InlineData(IdentitySegmentReasonCodes.QualificationMismatch)]
    [InlineData(IdentitySegmentReasonCodes.SegmentSuspended)]
    [InlineData(IdentitySegmentReasonCodes.AutoApprovalDisabled)]
    [InlineData(IdentitySegmentReasonCodes.PolicyUnavailable)]
    public void NothingDeclared_ASegmentThatDoesNotAllowTheApproval_IsAReviewInObservation(string reason)
    {
        var coherence = IdentityDocumentCoherences.Of(Report(NothingDeclared), Cie);
        Assert.Equal(IdentityDocumentCoherence.PassedWithoutBooking, coherence);

        var decision = Decide(coherence, EvidenceBundles.Observation with { ReasonCode = reason });

        Assert.Equal(IdentityFinalDecision.Review, decision.Final);
        Assert.Equal(IdentityShadowDecision.Approve, decision.Shadow);
        Assert.Equal(IdentityDecisionMode.Observation, decision.Mode);
        Assert.Equal(IdentityOutcomeCodes.Review, decision.OutcomeCode);
        Assert.Contains(NothingCompared, decision.Reasons);
        Assert.Equal(new VerificationReason(VerificationReasons.Checks.ObservationMode, reason), decision.Reasons[^1]);
    }

    [Fact]
    public void NotApplicable_SatisfiesTheRequirementOfTheBookingOnly_InTheRuleOfTheDecision()
    {
        var results = DocumentCoherenceResults.All.Append("maybe").ToList();
        foreach (var check in DocumentCoherenceReport.CarriedChecks)
        {
            foreach (var result in results)
            {
                var entries = Report(new CheckInIdentity("MARIA GIUSEPPINA", "ROSSI", new DateOnly(1985, 3, 12), "CA12345AA", "ITALIA")).Checks
                    .Select(entry => entry.Check == check ? entry with { Result = result } : entry)
                    .ToList();
                var report = DocumentCoherenceReport.FromChecks(entries);
                var coherence = IdentityDocumentCoherences.Of(report, Cie);
                var passes = result == DocumentCoherenceResults.Passed
                    || (result == DocumentCoherenceResults.NotApplicable && check == DocumentFormatChecks.BookingConsistency);

                Assert.True(passes == (coherence is IdentityDocumentCoherence.Passed or IdentityDocumentCoherence.PassedWithoutBooking), $"{check}={result}: {coherence}");
                if (passes)
                {
                    var expectedPass = result == DocumentCoherenceResults.NotApplicable ? IdentityDocumentCoherence.PassedWithoutBooking : IdentityDocumentCoherence.Passed;
                    Assert.True(expectedPass == coherence, $"{check}={result}: {coherence}");
                }

                if (result == DocumentCoherenceResults.NotApplicable && check != DocumentFormatChecks.BookingConsistency)
                    Assert.Equal(DocumentCoherenceResults.Unknown, report.Summary(check));

                if (result == DocumentCoherenceResults.Failed)
                {
                    var expected = check switch
                    {
                        DocumentFormatChecks.SideConsistency => IdentityDocumentCoherence.IdentifyingConflict,
                        DocumentFormatChecks.MrzCheckDigits => IdentityDocumentCoherence.MrzInvalid,
                        DocumentFormatChecks.Expiry => IdentityDocumentCoherence.Expired,
                        DocumentFormatChecks.BookingConsistency => IdentityDocumentCoherence.BookingConflict,
                        _ => IdentityDocumentCoherence.Unknown,
                    };
                    Assert.True(expected == coherence, $"{check} failed: {coherence}");
                }
            }
        }

        var conflict = Report(new CheckInIdentity(null, null, null, "CA99999ZZ", null));
        IReadOnlyList<string>[] lists =
        [
            Cie.RequiredChecks,
            [.. Cie.RequiredChecks.Where(check => check != DocumentFormatChecks.BookingConsistency)],
            [DocumentFormatChecks.MrzCheckDigits],
            [DocumentFormatChecks.Layout],
        ];
        foreach (var list in lists)
            Assert.Equal(IdentityDocumentCoherence.BookingConflict, IdentityDocumentCoherences.Of(conflict, Cie with { RequiredChecks = list }));

        Assert.Equal(IdentityDocumentCoherence.Unknown, IdentityDocumentCoherences.Of(Report(NothingDeclared), null));
    }

    [Fact]
    public void QualificationRunner_WritesABookingConflict_AsItsOwnCode_NeverAsAPassedDocument()
    {
        var conflict = QualificationRunner.CoherenceOf(Report(new CheckInIdentity(null, null, null, "CA99999ZZ", null)), Cie);
        Assert.Equal(IdentityDocumentCoherence.BookingConflict, conflict);
        Assert.Equal("booking_conflict", QualificationRunner.CoherenceCode(conflict));
        Assert.Equal(QualificationDecisionCodes.CoherenceBookingConflict, QualificationRunner.CoherenceCode(conflict));

        foreach (var declared in new[] { NothingDeclared, new CheckInIdentity("MARIA GIUSEPPINA", "ROSSI", null, null, null) })
            Assert.Equal(QualificationDecisionCodes.CoherencePassed, QualificationRunner.CoherenceCode(QualificationRunner.CoherenceOf(Report(declared), Cie)));
    }

    [Fact]
    public void QualificationRunner_GivesEveryCoherenceOfTheDecision_ItsCode()
    {
        var expected = new Dictionary<IdentityDocumentCoherence, string>
        {
            [IdentityDocumentCoherence.Unknown] = QualificationDecisionCodes.CoherenceUnknown,
            [IdentityDocumentCoherence.Passed] = QualificationDecisionCodes.CoherencePassed,
            [IdentityDocumentCoherence.IdentifyingConflict] = QualificationDecisionCodes.CoherenceIdentifyingConflict,
            [IdentityDocumentCoherence.MrzInvalid] = QualificationDecisionCodes.CoherenceMrzInvalid,
            [IdentityDocumentCoherence.Expired] = QualificationDecisionCodes.CoherenceExpired,
            [IdentityDocumentCoherence.BookingConflict] = QualificationDecisionCodes.CoherenceBookingConflict,
            [IdentityDocumentCoherence.PassedWithoutBooking] = QualificationDecisionCodes.CoherencePassed,
        };

        Assert.Equal(Enum.GetValues<IdentityDocumentCoherence>().Order(), expected.Keys.Order());
        foreach (var (coherence, code) in expected)
            Assert.Equal(code, QualificationRunner.CoherenceCode(coherence));

        Assert.Equal(2, expected.Values.Count(code => code == QualificationDecisionCodes.CoherencePassed));
        Assert.Equal(expected.Count - 1, expected.Values.Distinct().Count());
    }

    [Fact]
    public async Task Closure_NothingDeclared_IsApproved_AConflictIsReviewedWithItsCode_AndTheCountersAndThePilotFileKeepTheirShape()
    {
        await using var world = await PresenceClosureWorld.CreateAsync();
        world.Segment = EvidenceBundles.Allowed;

        var approved = await world.ReadyStayAsync();
        await KeepCoherenceAsync(world, approved, Report(NothingDeclared).Checks);
        var ok = await world.Flow().SubmitPresenceSequenceAsync(approved, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), await world.IssueChallengeAsync(approved)), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        var accepted = Assert.Single(await world.LogRowsAsync(approved));
        Assert.Equal((BiometricOutcome.Accept, BiometricOutcome.Accept, IdentityDecisionMode.Automatic), (accepted.Outcome, accepted.ShadowOutcome, accepted.DecisionMode));
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.Passed), VerificationReasons.Parse(accepted.ReasonsJson));
        Assert.Contains(NothingCompared, VerificationReasons.Parse(accepted.ReasonsJson));
        var approvedSaved = await world.ReadBackAsync(approved.Id);
        Assert.Equal(IdentityOutcomeCodes.AutoApproved, approvedSaved.IdentityOutcomeCode);
        Assert.Null(approvedSaved.OperatorRequestedAt);

        var reviewed = await world.ReadyStayAsync();
        await KeepCoherenceAsync(world, reviewed, Report(new CheckInIdentity(null, null, null, "CA99999ZZ", null)).Checks);
        var toPerson = await world.Flow().SubmitPresenceSequenceAsync(reviewed, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), await world.IssueChallengeAsync(reviewed)), Ct);

        Assert.Equal(IdentityVerificationGuestResults.Operator, toPerson.Result);
        var escalated = Assert.Single(await world.LogRowsAsync(reviewed));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate, IdentityDecisionMode.Automatic), (escalated.Outcome, escalated.ShadowOutcome, escalated.DecisionMode));
        Assert.Contains(
            new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.BookingMismatch),
            VerificationReasons.Parse(escalated.ReasonsJson));
        var reviewedSaved = await world.ReadBackAsync(reviewed.Id);
        Assert.Equal(IdentityOutcomeCodes.Review, reviewedSaved.IdentityOutcomeCode);
        Assert.NotNull(reviewedSaved.OperatorRequestedAt);
        Assert.Equal([(reviewed.Id, VerificationAlertReasons.Review)], world.Alerts.Raised);

        var records = await IdentityVerificationLedger.ReadAsync(
            world.Db,
            new HostScope(world.OrgId),
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc),
            PresenceClosureKit.Now.UtcDateTime,
            TimeSpan.FromHours(24),
            withReasons: true,
            Ct);
        var metrics = IdentityVerificationMetricsService.Summarize(records);
        Assert.Equal(2, metrics.Opened);
        Assert.Equal((1, 1), (metrics.Outcomes.AutoApproved, metrics.Outcomes.Review));
        Assert.Equal(metrics.Opened, metrics.Outcomes.Total);
        Assert.Equal(metrics.Opened, IdentityOutcomeCategories.All.Sum(category => metrics.Outcomes.Of(category)));

        Assert.Equal(
            [
                "attempt_ref", "property_id", "opened_at", "closed_at", "format_id", "channel", "outcome_code", "decision_mode", "shadow_outcome", "reason_codes",
                "guided_retries", "human_outcome", "human_decided_at", "review_seconds", "duration_ms", "policy_version", "policy_hash_prefix",
            ],
            IdentityPilotExport.Columns);
        var reasonColumn = IdentityPilotExport.Columns.ToList().IndexOf("reason_codes");
        foreach (var record in records)
        {
            var cells = IdentityPilotExport.Line(record).Split(',');
            Assert.Equal(IdentityPilotExport.Columns.Count, cells.Length);
            if (record.Outcome == IdentityOutcomeCategory.AutoApproved)
                Assert.Equal("booking_consistency:not_applicable", cells[reasonColumn]);
            else
                Assert.Equal("document_coherence:booking_mismatch", cells[reasonColumn]);
        }

        foreach (var text in records.SelectMany(record => new[] { IdentityPilotExport.Line(record), record.Decided?.ReasonsJson ?? string.Empty }))
        {
            Assert.DoesNotContain("CA99999ZZ", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(IdentityTestDocuments.DocumentNumber, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(IdentityTestDocuments.Surname, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(IdentityTestDocuments.GivenNames, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Closure_InTicino_NothingDeclared_IsStillReviewedByAPerson()
    {
        await using var world = await PresenceClosureWorld.CreateAsync();
        world.Segment = EvidenceBundles.Allowed;
        var session = await world.ReadyStayAsync(keepsNoDocument: true);
        var submission = PresenceClosureKit.Submission(
            PresenceClosureKit.NewKey(),
            await world.IssueChallengeAsync(session),
            documentPhoto: new MemoryStream(PresenceClosureKit.FrontPicture),
            documentPhotoBack: new MemoryStream(PresenceClosureKit.BackPicture));

        var answer = await world.Flow().SubmitPresenceSequenceAsync(session, submission, Ct);

        Assert.Equal(IdentityVerificationGuestResults.Operator, answer.Result);
        var row = Assert.Single(await world.LogRowsAsync(session));
        Assert.Equal((BiometricOutcome.Escalate, BiometricOutcome.Escalate), (row.Outcome, row.ShadowOutcome));
        Assert.Contains(new VerificationReason(VerificationReasons.Checks.DocumentCoherence, VerificationReasons.Results.NotRun), VerificationReasons.Parse(row.ReasonsJson));
        Assert.Equal(IdentityOutcomeCodes.Review, (await world.ReadBackAsync(session.Id)).IdentityOutcomeCode);
    }

    private static async Task KeepCoherenceAsync(PresenceClosureWorld world, GuestCheckInSession session, IReadOnlyList<DocumentCoherenceCheck> entries)
    {
        session.DocumentCheckJson = JsonSerializer.Serialize(PresenceClosureKit.ReadCheck(session) with { Coherence = entries }, Web);
        await world.Db.SaveChangesAsync();
    }
}
