using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US03/T01 (NF-189, NF-190, NF-191): the document step of the self-hosted flow, through the real flow service, the real geometry inspector, the real classifier of the side, the
/// real checker of the coherence, the real registry of the formats (the Italian card) and the real policy file, with the readers of the MRZ and of the zones scripted and the pictures
/// built in code. A picture with a guidance code is refused and is not an attempt; after the maximum of refusals of a side a person decides; a duplicated or wrong side is told apart; an MRZ
/// of a format that is not in the registry closes the verification as <c>format_unsupported</c>; the coherence is kept as flags and never stops the step; nothing a document says is kept or
/// logged; with the flow off none of it runs.
/// </summary>
public sealed class DocumentStepSelfHostedTests
{
    private const string ServedVersion = "TEST-biometric-v1";

    // What the documents of these tests say: invented, and distinct enough that none can be found in a stored JSON or a log line by chance.
    internal const string Surname = "VERDEGIALLO";
    internal const string GivenNames = "ZEFIRINO";
    internal const string DocumentNumber = "CAZ99887Q";
    internal const string BirthDay = "1987-03-19";
    internal const string ExpiryDay = "2033-06-30";

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly JsonSerializerOptions FlowJson = new(JsonSerializerDefaults.Web);

    internal static readonly DocumentFormat Cie = new DocumentFormatRegistry(
        Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, System.AppContext.BaseDirectory).Get("it-cie-3.0")!;

    // ---- AC1, AC4: the sides are checked, kept as flags, and the document is complete only with both ------------------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_FrontThenBack_KeepsTheChecksAsFlags_AndTheDocumentIsCompleteOnlyWithBothSides()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var front = Picture(DocumentSide.Front);
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();

        var first = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(front), DocumentSide.Front, Ct);

        // The front alone: kept, checked, and the back is still to come. The format is not known before an MRZ is read.
        Assert.False(first.Acceptable);
        Assert.Contains(CaptureGuidanceCodes.DocumentBackNeeded, first.GuidanceCodes);
        Assert.True(first.DocumentStored);
        var afterFront = await world.ReadBackAsync(session.Id);
        var frontCheck = ReadCheck(afterFront);
        Assert.True(frontCheck.Front!.GeometryOk);
        Assert.Equal("front", frontCheck.Front.ClassifiedSide);
        Assert.Equal(16, frontCheck.Front.DHash!.Length);
        Assert.Null(frontCheck.Coherence);
        Assert.Null(afterFront.IdentityFormatId);
        var midState = await world.Flow().GetStateAsync(session, Ct);
        Assert.False(midState.DocumentChecked);
        Assert.Equal(IdentityVerificationSteps.Document, midState.Step);

        var second = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        // Both sides passed their checks: the format the MRZ showed is kept, the coherence is in, and the step moves on. No attempt was spent.
        Assert.True(second.Acceptable);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal("it-cie-3.0", saved.IdentityFormatId);
        Assert.Equal(0, saved.BiometricAttempts);
        var check = ReadCheck(saved);
        Assert.True(check.Back!.GeometryOk);
        Assert.Equal("back", check.Back.ClassifiedSide);
        Assert.Equal(16, check.Back.DHash!.Length);
        Assert.NotNull(check.Coherence);
        foreach (var field in new[] { ZonedFieldNames.Surname, ZonedFieldNames.GivenNames, ZonedFieldNames.BirthDate, ZonedFieldNames.DocumentNumber })
            Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Passed, field), check.Coherence);

        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.Passed), check.Coherence);
        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.Expiry, DocumentCoherenceResults.Passed, ZonedFieldNames.ExpiryDate), check.Coherence);
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.True(state.DocumentChecked);
        Assert.Equal("it-cie-3.0", state.FormatId);
        Assert.Equal(IdentityVerificationSteps.Selfie, state.Step);
        Assert.Equal(["front", "back"], state.DocumentSides);
    }

    [Fact]
    public async Task DocumentStep_ADocumentWrittenBeforeTheFlowWasOn_IsNotCompleteForIt()
    {
        // A picture kept with no check of the self-hosted flow (the flow was off when it came) does not count: the document is asked again.
        await using var world = await World.CreateAsync(FeatureFlags.CheckInIdentityVerification);
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct)).DocumentStored);
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct)).Acceptable);
        Assert.True((await world.Flow().GetStateAsync(session, Ct)).DocumentChecked);

        world.Flags.Set(FeatureFlags.CheckInIdentitySelfHosted, true);

        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.False(state.DocumentChecked);
        Assert.Equal(IdentityVerificationSteps.Document, state.Step);
    }

    // ---- AC1: a picture with a guidance code is refused, is not an attempt, and after the maximum a person decides -----------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_APictureWithAGuidanceCode_IsRefusedWithThatCode_AndIsNotAnAttempt()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var cutByTheFrame = Picture(DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303));

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(cutByTheFrame), DocumentSide.Front, Ct);

        Assert.False(result.Acceptable);
        Assert.Equal([IdentityGuidanceCodes.DocumentEdgesNotVisible], result.GuidanceCodes);
        Assert.False(result.DocumentStored);
        Assert.Equal(DocumentReadingMethod.None, result.Reading.Method);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(0, saved.BiometricAttempts);
        Assert.Equal(new Dictionary<string, int> { ["front"] = 1 }, ReadCheck(saved).Refusals);
        Assert.Null(ReadCheck(saved).Front);
        Assert.Empty(world.Storage.Objects);
        Assert.Null(saved.OperatorRequestedAt);
        Assert.Null(saved.IdentityClosedAt);
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.False(state.DocumentChecked);
        Assert.Equal(IdentityVerificationSteps.Document, state.Step);
        Assert.Empty(state.DocumentSides!);
    }

    [Fact]
    public async Task DocumentStep_AfterTheMaximumOfRefusalsOfASide_AnswersUnreadable_AndAPersonDecides()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var nothing = PlainPicture();
        var maximum = world.Policies.Snapshot.Policy!.Document.MaxRecapturesPerSide;
        Assert.True(maximum > 0);

        // The picture may be captured again `maximum` times after its first refusal: every one of them is refused with its own code and counted...
        for (var refusal = 1; refusal <= maximum; refusal++)
        {
            var refused = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(nothing), DocumentSide.Front, Ct);
            Assert.Contains(IdentityGuidanceCodes.DocumentNotFound, refused.GuidanceCodes);
            Assert.DoesNotContain(IdentityGuidanceCodes.DocumentUnreadable, refused.GuidanceCodes);
            Assert.Equal(refusal, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals!["front"]);
        }

        Assert.Empty(world.Alerts);

        // ...and the one that goes beyond them is the answer «unreadable»: a person decides, and the verification is closed as a review.
        var unreadable = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(nothing), DocumentSide.Front, Ct);

        Assert.Equal([IdentityGuidanceCodes.DocumentUnreadable], unreadable.GuidanceCodes);
        Assert.False(unreadable.Acceptable);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.NotNull(saved.IdentityClosedAt);
        Assert.Equal(IdentityOutcomeCodes.Review, saved.IdentityOutcomeCode);
        Assert.Equal(0, saved.BiometricAttempts);
        Assert.Equal([(session.Id, VerificationAlertReasons.Review)], world.Alerts);
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.Equal(IdentityVerificationSteps.Operator, state.Step);
        Assert.True(state.Closed);
        Assert.Equal(IdentityVerificationGuestResults.Operator, state.Outcome);
        Assert.True(state.OperatorPathOpen);
    }

    [Fact]
    public async Task DocumentStep_ARefusedPicture_DoesNotReplaceTheSideAlreadyKept()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct)).DocumentStored);
        var keptHash = ReadCheck(await world.ReadBackAsync(session.Id)).PhotoSha256;
        var kept = world.Storage.Objects.Keys.ToList();

        var refused = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303))), DocumentSide.Front, Ct);

        Assert.Contains(IdentityGuidanceCodes.DocumentEdgesNotVisible, refused.GuidanceCodes);
        var check = ReadCheck(await world.ReadBackAsync(session.Id));
        Assert.Equal(keptHash, check.PhotoSha256);
        Assert.True(check.Front!.GeometryOk);
        Assert.Equal(1, check.Refusals!["front"]);
        Assert.Equal(kept, world.Storage.Objects.Keys.ToList());
        Assert.Equal(["front"], refused.SidesReceived);
    }

    // ---- AC2: a duplicated side and a wrong side ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_TheSamePictureForTheOtherSide_IsADuplicate_AndAFrontSentAsTheBackIsTheWrongSide()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var front = Picture(DocumentSide.Front);
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(front), DocumentSide.Front, Ct)).DocumentStored);

        // The same file sent as the back of the card: the same side twice.
        var duplicate = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(front), DocumentSide.Back, Ct);

        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, duplicate.GuidanceCodes[0]);
        Assert.False(duplicate.Acceptable);
        Assert.Equal(["front"], duplicate.SidesReceived);

        // Another session, nothing sent before: a front declared as the back is the wrong side, and not a duplicate.
        var other = await world.StayAsync("Zeno", "Bianchi", ready: true);
        var wrong = await world.Flow().SubmitDocumentAsync(other, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Back, Ct);

        Assert.Equal(IdentityGuidanceCodes.DocumentWrongSide, wrong.GuidanceCodes[0]);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentSideDuplicate, wrong.GuidanceCodes);
        Assert.False(wrong.Acceptable);
        Assert.Equal(0, (await world.ReadBackAsync(other.Id)).BiometricAttempts);
    }

    [Fact]
    public async Task DocumentStep_ASideThatTheFormatDoesNotHave_IsRefusedAsTheWrongSide()
    {
        // The Italian card has a front and a back: one picture «of the document» (the side of a passport) is not a side of it.
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Single, Ct);

        Assert.Equal([IdentityGuidanceCodes.DocumentWrongSide], result.GuidanceCodes);
        Assert.False(result.DocumentStored);
        Assert.Equal(new Dictionary<string, int> { ["single"] = 1 }, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals);
    }

    // ---- AC3: a format that is not in the registry goes to a person -------------------------------------------------------------------------------------------------------

    /// <summary>Proof (data): an MRZ of a format the registry does not hold sends the session to a person, with the outcome <c>format_unsupported</c> that the counters keep apart.</summary>
    [Fact]
    public async Task DocumentStep_UnregisteredMrzFormat_ClosesAsFormatUnsupported()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var passport = Picture(DocumentSide.Back);
        world.Mrz.Add(passport, CieMrz(read =>
        {
            read.DocumentType = "Passport";
            read.DocumentCode = "P<";
            read.MrzType = "TD3";
        }));

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(passport), DocumentSide.Single, Ct);

        Assert.Equal([IdentityGuidanceCodes.DocumentFormatUnsupported], result.GuidanceCodes);
        Assert.False(result.Acceptable);
        Assert.False(result.DocumentStored);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityOutcomeCodes.FormatUnsupported, saved.IdentityOutcomeCode);
        Assert.NotNull(saved.IdentityClosedAt);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.Null(saved.IdentityFormatId);
        Assert.Equal(0, saved.BiometricAttempts);
        Assert.Null(saved.BiometricOutcome);
        Assert.Empty(world.Storage.Objects);
        Assert.Equal([(session.Id, VerificationAlertReasons.Review)], world.Alerts);
        Assert.Empty(world.Zoned.Cards);
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.Equal(IdentityVerificationSteps.Operator, state.Step);
        Assert.True(state.Closed);
        Assert.Null(state.FormatId);
        Assert.Equal(IdentityVerificationGuestResults.Operator, state.Outcome);
    }

    [Fact]
    public async Task DocumentStep_ACardOfAnotherCountryAndAResidencePermitOfTheSameCode_AreNotTheItalianCard()
    {
        await using var world = await World.CreateAsync();
        foreach (var (code, state) in new[] { ("I<", "UTO"), ("IR", "ITA") })
        {
            var session = await world.StayAsync("Anna", "Bianchi", ready: true);
            var picture = Picture(DocumentSide.Back, new DocumentImageFactory.Placement(80 + code.Length, 70, 480, 303));
            world.Mrz.Add(picture, CieMrz(read =>
            {
                read.DocumentCode = code;
                read.IssuingState = state;
            }));

            var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(picture), DocumentSide.Back, Ct);

            Assert.Equal([IdentityGuidanceCodes.DocumentFormatUnsupported], result.GuidanceCodes);
            Assert.Equal(IdentityOutcomeCodes.FormatUnsupported, (await world.ReadBackAsync(session.Id)).IdentityOutcomeCode);
        }
    }

    /// <summary>
    /// A doubtful MRZ (a check digit that fails) of a code the registry does not know is no proof of an unsupported format: the document code and the issuing state are covered by no check
    /// digit, so one character the OCR read wrong must not close the verification for good. The picture is asked again, refused with the guidance of an unreadable reading and counted.
    /// </summary>
    [Fact]
    public async Task DocumentStep_ADoubtfulMrzOfAnUnknownCode_IsRefusedAndCounted_NotClosedForGood()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz(read =>
        {
            read.DocumentCode = "ZZ";
            read.CheckDigitsValid = false;
            read.Confidence = 0.40;
        }));

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        Assert.False(result.Acceptable);
        Assert.False(result.DocumentStored);
        Assert.Contains(CaptureGuidanceCodes.ShowWholeDocument, result.GuidanceCodes);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentFormatUnsupported, result.GuidanceCodes);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Null(saved.IdentityClosedAt);
        Assert.Null(saved.IdentityOutcomeCode);
        Assert.Null(saved.OperatorRequestedAt);
        Assert.Null(saved.IdentityFormatId);
        Assert.Equal(new Dictionary<string, int> { ["back"] = 1 }, ReadCheck(saved).Refusals);
        Assert.Equal(0, saved.BiometricAttempts);
        Assert.Empty(world.Alerts);
        Assert.Empty(world.Storage.Objects);
    }

    // ---- AC4: the coherence is kept as flags and never stops the step ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_AnIdentifyingConflictAnExpiredDocumentAndAnInvalidMrz_DoNotStopTheStep_AndAreKeptAsFlags()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz(read =>
        {
            read.CheckDigitsValid = false;
            read.Confidence = 0.40;
            read.ExpirationDate = new DateTime(2025, 1, 31);
            read.IsExpired = true;
        }));
        // The surname printed on the front is not the one of the MRZ.
        world.Zoned.Fields[DocumentSide.Front] = FrontFields(printedSurname: "DIVERSOCOGNOME");
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct)).DocumentStored);

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        // The step completes, the conflict, the invalid MRZ and the expiry are in the record for the decision.
        Assert.True(result.Acceptable);
        var check = ReadCheck(await world.ReadBackAsync(session.Id));
        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Failed, ZonedFieldNames.Surname), check.Coherence!);
        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.SideConsistency, DocumentCoherenceResults.Passed, ZonedFieldNames.GivenNames), check.Coherence!);
        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.MrzCheckDigits, DocumentCoherenceResults.Failed), check.Coherence!);
        Assert.Contains(new DocumentCoherenceCheck(DocumentFormatChecks.Expiry, DocumentCoherenceResults.Failed, ZonedFieldNames.ExpiryDate), check.Coherence!);
        var report = DocumentCoherenceReport.FromChecks(check.Coherence!);
        Assert.True(report.IdentifyingConflict);
        Assert.True(report.Expired);
        Assert.False(report.MrzValid);
        Assert.Equal([IdentityGuidanceCodes.DocumentFieldConflict, IdentityGuidanceCodes.DocumentExpired], report.Codes);
        Assert.True((await world.Flow().GetStateAsync(session, Ct)).DocumentChecked);
        Assert.Null((await world.ReadBackAsync(session.Id)).OperatorRequestedAt);
    }

    [Fact]
    public async Task DocumentStep_TheOtherSideNoLongerKept_LeavesTheCoherenceUnknown_AndTheStepCompletes()
    {
        // A property that keeps no document has nothing to read the other side from: the two sides are never in memory together, and the checks across them are not stored
        // (an unknown is never a pass); the step completes all the same.
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct)).DocumentStored);
        world.Storage.Objects.Clear();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        Assert.True(result.Acceptable);
        Assert.Null(ReadCheck(await world.ReadBackAsync(session.Id)).Coherence);
        Assert.True((await world.Flow().GetStateAsync(session, Ct)).DocumentChecked);
    }

    // ---- The canonical card ------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_TheReaderOfTheZonesIsHandedTheCardCutOutAndUpright_AlsoWhenThePictureIsTurned()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.TurnedCounterClockwise(upright);
        var turnedPicture = TestImages.Png(turned.Width, turned.Height, turned.Rgb);

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(turnedPicture), DocumentSide.Front, Ct);

        // The picture is 480 by 640, the card is not: the zones are read on the card, landscape, with its portrait where the layout says.
        Assert.True(result.DocumentStored);
        var card = Assert.Single(world.Zoned.Cards);
        Assert.Equal(DocumentSide.Front, card.Side);
        Assert.InRange(card.Width, 470, 495);
        Assert.InRange(card.Height, 295, 315);
        Assert.True(card.PortraitIsWhereTheLayoutSaysIt);
        Assert.Equal(Cie.Id, card.FormatId);
    }

    // ---- D8: a fault never approves -----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_AModelThatFails_SendsTheVerificationToAPerson_AndNothingIsApproved()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        world.Detector.Fail = new BiometricUnavailableException();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct);

        Assert.Equal([IdentityGuidanceCodes.DocumentUnreadable], result.GuidanceCodes);
        Assert.False(result.Acceptable);
        Assert.False(result.DocumentStored);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityOutcomeCodes.TechnicalError, saved.IdentityOutcomeCode);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.Null(saved.BiometricOutcome);
        Assert.Equal([(session.Id, VerificationAlertReasons.TechnicalError)], world.Alerts);
        Assert.Empty(world.Storage.Objects);
        Assert.Contains(world.Logs, line => line.Contains(nameof(BiometricUnavailableException), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("policy-missing")]
    [InlineData("policy-not-pinned")]
    [InlineData("no-format")]
    public async Task DocumentStep_APolicyOrARegistryThatCannotBeUsed_SendsTheVerificationToAPerson(string fault)
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        world.Break(fault);

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct);

        Assert.Equal([IdentityGuidanceCodes.DocumentUnreadable], result.GuidanceCodes);
        Assert.False(result.Acceptable);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityOutcomeCodes.Unavailable, saved.IdentityOutcomeCode);
        Assert.NotNull(saved.OperatorRequestedAt);
        Assert.Null(saved.BiometricOutcome);
        Assert.Empty(world.Storage.Objects);
    }

    // ---- Privacy: flags and hashes, never a value --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Proof (privacy): after a full two-sided submission, a refusal and a hand-off, <c>DocumentCheckJson</c> and every line of the log hold the names of the checks, flags, hashes and
    /// counts, and not one value a document says (the surname, the given names, the number, the dates).
    /// </summary>
    [Fact]
    public async Task DocumentStep_StoresFlagsOnly_NoReadValue()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();

        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct);
        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);
        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303))), DocumentSide.Front, Ct);

        var saved = await world.ReadBackAsync(session.Id);
        var check = ReadCheck(saved);
        Assert.NotNull(check.Coherence);
        Assert.NotEmpty(saved.DocumentCheckJson!);
        var values = new[] { Surname, GivenNames, DocumentNumber, BirthDay, BirthDay.Replace("-", string.Empty, StringComparison.Ordinal), ExpiryDay, ExpiryDay.Replace("-", string.Empty, StringComparison.Ordinal) };
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, saved.DocumentCheckJson!, StringComparison.OrdinalIgnoreCase);
            Assert.All(world.Logs, line => Assert.DoesNotContain(value, line, StringComparison.OrdinalIgnoreCase));
        }

        // What the record keeps of the coherence: the check, its result and the NAME of the field.
        using var json = JsonDocument.Parse(saved.DocumentCheckJson!);
        foreach (var entry in json.RootElement.GetProperty("coherence").EnumerateArray())
            Assert.Equal(["check", "field", "result"], entry.EnumerateObject().Select(member => member.Name).Order());

        // And of a picture: flags, the side it showed and its hash.
        Assert.Equal(
            ["classifiedSide", "dHash", "face", "geometryOk", "mrz", "numberProof", "qualityOk"],
            json.RootElement.GetProperty("front").EnumerateObject().Select(member => member.Name).Order());
        Assert.NotEmpty(world.Logs);
    }

    // ---- AC6: with the flow off nothing of it runs -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_FlowOff_RunsNoneOfTheChecks_AndKeepsNoNewMember()
    {
        await using var world = await World.CreateAsync(FeatureFlags.CheckInIdentityVerification);
        var session = await world.ConsentedStayAsync();
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        // A card cut by the frame, which the checks of the flow would refuse: as it always was, the reading of today accepts and keeps it.
        var picture = Picture(DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303));

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(picture), DocumentSide.Front, Ct);

        Assert.True(result.DocumentStored);
        Assert.Contains(CaptureGuidanceCodes.DocumentBackNeeded, result.GuidanceCodes);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentEdgesNotVisible, result.GuidanceCodes);
        Assert.Equal(0, world.Detector.Calls);
        Assert.Empty(world.Zoned.Cards);
        var saved = await world.ReadBackAsync(session.Id);
        var check = ReadCheck(saved);
        Assert.Null(check.Front!.GeometryOk);
        Assert.Null(check.Front.ClassifiedSide);
        Assert.Null(check.Front.DHash);
        Assert.Null(check.Refusals);
        Assert.Null(check.Coherence);
        Assert.Null(saved.IdentityFormatId);
        Assert.Null((await world.Flow().GetStateAsync(session, Ct)).FormatId);
    }

    [Fact]
    public async Task DocumentStep_AFlowBuiltWithoutTheServicesOfTheStep_ReadsTheDocumentAsBefore()
    {
        // The flow is on, but this instance was built by hand with none of the services of the document step (the application registers them all): nothing of the step runs and the
        // document is complete as it always was, so the other tasks of the flow keep their own tests.
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        var flow = world.Flow(wired: false);

        var first = await flow.SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303))), DocumentSide.Front, Ct);
        var second = await flow.SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        Assert.True(first.DocumentStored);
        Assert.True(second.Acceptable);
        Assert.Equal(0, world.Detector.Calls);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Null(ReadCheck(saved).Front!.GeometryOk);
        Assert.Null(saved.IdentityFormatId);
        Assert.True((await flow.GetStateAsync(session, Ct)).DocumentChecked);
    }

    [Fact]
    public async Task DocumentStep_WrittenBeforeTheNewMembers_StillReads()
    {
        // The JSON the flow wrote before this task: a two-sided card with its pictures and the proofs, none of the members of the document step.
        const string before = """
            {"check":{"mrzRead":true,"checkDigitsValid":true,"expired":false,"namesConsistent":true,"birthDateConsistent":true,"documentNumberConsistent":null,
            "nationalityConsistent":true,"holderIsMinor":false,"valid":true,"consistent":true},"photoSha256":"5F1A","checkedAt":"2026-11-20T10:00:00Z","backSha256":"9C0D",
            "front":{"face":true,"qualityOk":true,"mrz":null,"numberProof":null},"back":{"face":null,"qualityOk":true,"mrz":null,"numberProof":"3E7B"},
            "twoSided":true,"verifyingGuestPosition":1,"proofKey":"4A2F"}
            """;

        var read = JsonSerializer.Deserialize<StoredDocumentCheck>(before, FlowJson)!;

        Assert.Null(read.Refusals);
        Assert.Null(read.Coherence);
        Assert.Null(read.Front!.GeometryOk);
        Assert.Null(read.Front.ClassifiedSide);
        Assert.Null(read.Back!.DHash);
        Assert.Equal("3E7B", read.Back.NumberProof);
        Assert.True(read.TwoSided);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------------------------------------------------

    private static StoredDocumentCheck ReadCheck(GuestCheckInSession session)
    {
        return JsonSerializer.Deserialize<StoredDocumentCheck>(session.DocumentCheckJson!, FlowJson)!;
    }

    /// <summary>A picture of a side of the Italian card (a PNG), built in code: a whole card on a darker background unless <paramref name="placement"/> says another place.</summary>
    internal static byte[] Picture(DocumentSide side, DocumentImageFactory.Placement? placement = null)
    {
        using var image = DocumentImageFactory.Render(Cie, side, placement ?? DocumentImageFactory.WholeCard);
        return TestImages.Png(image.Width, image.Height, image.Rgb);
    }

    /// <summary>A picture with no card in it: one gray level and nothing else.</summary>
    internal static byte[] PlainPicture()
    {
        using var image = DocumentImageFactory.Plain(120);
        return TestImages.Png(image.Width, image.Height, image.Rgb);
    }

    /// <summary>What the MRZ of the card says: a valid Italian identity card of an adult, not expired, of the format of the registry.</summary>
    internal static MrzExtractionResult CieMrz(Action<MrzExtractionResult>? edit = null)
    {
        var result = new MrzExtractionResult
        {
            DocumentType = "IdCard",
            DocumentCode = "I<",
            MrzType = "TD1",
            IssuingState = "ITA",
            Surname = Surname,
            GivenNames = GivenNames,
            DocumentNumber = DocumentNumber,
            DateOfBirth = DateTime.Parse(BirthDay, System.Globalization.CultureInfo.InvariantCulture),
            ExpirationDate = DateTime.Parse(ExpiryDay, System.Globalization.CultureInfo.InvariantCulture),
            IsExpired = false,
            Nationality = "ITA",
            Sex = "F",
            CheckDigitsValid = true,
            Confidence = 0.95,
        };
        edit?.Invoke(result);
        return result;
    }

    /// <summary>What the zones of the front say, as the reader of the zones gives it (the printed values of the same card).</summary>
    internal static IReadOnlyList<ZonedField> FrontFields(string printedSurname = Surname) =>
    [
        Printed(ZonedFieldNames.Surname, printedSurname),
        Printed(ZonedFieldNames.GivenNames, GivenNames),
        Printed(ZonedFieldNames.BirthDate, BirthDay),
        Printed(ZonedFieldNames.DocumentNumber, DocumentNumber),
        Printed(ZonedFieldNames.ExpiryDate, ExpiryDay),
        Printed(ZonedFieldNames.Sex, "F"),
        Printed(ZonedFieldNames.Nationality, "ITA"),
    ];

    private static ZonedField Printed(string name, string value) =>
        new(name, value, DocumentSide.Front, name, DocumentOcrService.FallbackConfidence, ZonedFieldSources.Viz);

    /// <summary>One org on the Agency plan, a property in Italy, the flow with its readers scripted and the self-hosted flow on.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"document-step-{Guid.NewGuid()}").Options;

        // One provider for every world: the model of the context is cached by it (DataProtectionModelCacheKeyFactory), so the model is built once and not once per test.
        private static readonly IDataProtectionProvider SharedProtection = new EphemeralDataProtectionProvider();

        private readonly IDataProtectionProvider _dataProtection = SharedProtection;
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly Mock<ICheckInLegalTexts> _legalTexts = ActivePoseFlowTestVerifier.CreateRegistry();
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "casazen-document-step-" + Guid.NewGuid().ToString("N"));
        private DocumentFormatRegistry _formats;
        private IdentityPolicyProvider _policies;

        private World(string[] flags)
        {
            Db = new AppDbContext(_options, null, _dataProtection);
            Flags = new TestFeatureFlags(flags);
            Detector = new SwitchableDetector();
            _formats = new DocumentFormatRegistry(Options.Create(new IdentityVerificationOptions()), NullLogger<DocumentFormatRegistry>.Instance, System.AppContext.BaseDirectory);
            _policies = ShippedPolicies();
            Directory.CreateDirectory(_folder);
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public TestFeatureFlags Flags { get; }

        public FakeTimeProvider Clock { get; } = new(Now);

        public SwitchableDetector Detector { get; }

        public ScriptedMrz Mrz { get; } = new();

        public ScriptedZonedReader Zoned { get; } = new();

        public MemoryStorage Storage { get; } = new();

        public RecordingAlerts Alerts { get; } = new();

        public List<string> Logs { get; } = [];

        public IdentityPolicyProvider Policies => _policies;

        public static async Task<World> CreateAsync(params string[] flags)
        {
            var world = new World(flags.Length == 0 ? [FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted] : flags);
            world.Db.Orgs.Add(new OrgEntity
            {
                Id = world.OrgId,
                Name = "Agenzia documenti",
                Slug = $"documenti-{world.OrgId:N}"[..20],
                DisplayName = "Agenzia documenti",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Agency,
            });
            world.Db.Properties.Add(new Property
            {
                Id = world._propertyId,
                OrgId = world.OrgId,
                OwnerId = "auth0|owner",
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Country = "IT",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            });
            await world.Db.SaveChangesAsync(Ct);
            return world;
        }

        /// <summary>The policy of the application: its committed file, pinned by its canonical hash (the build output has the file next to the assembly).</summary>
        private static IdentityPolicyProvider ShippedPolicies(string pin = IdentityPolicyTestFiles.ShippedCanonicalHash) =>
            new(Options.Create(new IdentityVerificationOptions { PolicySha256 = pin }), NullLogger<IdentityPolicyProvider>.Instance, System.AppContext.BaseDirectory);

        /// <summary>Makes a part of the step unusable: a policy file that is not there, a policy that is not pinned, a registry with no format.</summary>
        public void Break(string fault)
        {
            switch (fault)
            {
                case "policy-missing":
                    _policies = new IdentityPolicyProvider(
                        Options.Create(new IdentityVerificationOptions { PolicyFile = "IdentityPolicies/not-there.json", PolicySha256 = IdentityPolicyTestFiles.ShippedCanonicalHash }),
                        NullLogger<IdentityPolicyProvider>.Instance,
                        System.AppContext.BaseDirectory);
                    break;
                case "policy-not-pinned":
                    _policies = ShippedPolicies(pin: string.Empty);
                    break;
                default:
                    _formats = new DocumentFormatRegistry(
                        Options.Create(new IdentityVerificationOptions { FormatsDirectory = "NoFormatsHere" }), NullLogger<DocumentFormatRegistry>.Instance, _folder);
                    break;
            }

            Assert.False(fault != "no-format" ? _policies.Snapshot.IsReady : _formats.All.Count > 0);
        }

        /// <summary>The flow with its services; <paramref name="wired"/> false builds it by hand without the ones of the document step, as a tool or a test of another task does.</summary>
        public IdentityVerificationFlowService Flow(bool wired = true)
        {
            var zoned = Zoned;
            var inspector = new DocumentGeometryInspector(Detector, Options.Create(new BiometricModelOptions()));
            return new IdentityVerificationFlowService(
                Db,
                new ScriptedAgent(),
                Alerts,
                Mock.Of<IAccessCodeService>(),
                Mock.Of<IEmailQueue>(),
                Mrz,
                new EvidentDocumentOcr(),
                new Roster(),
                Storage,
                _dataProtection,
                Clock,
                new ListLogger<IdentityVerificationFlowService>(Logs),
                legalTexts: _legalTexts.Object,
                activePose: new ActivePoseFlowTestVerifier(),
                featureFlags: Flags,
                documentPictures: wired ? new DocumentPictureAnalyzer(inspector, zoned, Options.Create(new BiometricModelOptions())) : null,
                sideClassifier: wired ? new DocumentSideClassifier() : null,
                coherenceChecker: wired ? new DocumentCoherenceChecker() : null,
                formats: wired ? _formats : null,
                identityPolicy: wired ? _policies : null);
        }

        /// <summary>A confirmed stay of a booker, with an open check-in session, and the consent to the served text recorded.</summary>
        public async Task<GuestCheckInSession> ConsentedStayAsync()
        {
            return await StayAsync(GivenNames, Surname, ready: true);
        }

        public async Task<GuestCheckInSession> StayAsync(string firstName, string lastName, bool ready = false)
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                FirstName = firstName,
                LastName = lastName,
                Email = $"{firstName.ToLowerInvariant()}@example.com",
                DateOfBirth = new DateTime(1987, 3, 19),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                PropertyId = _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 30),
                CheckOutDate = new DateTime(2026, 12, 2),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            var session = new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = OrgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.UtcDateTime.AddDays(7),
                Status = GuestCheckInSessionStatus.InCompilazione,
                Language = "it",
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            await Db.SaveChangesAsync(Ct);

            var loaded = await Db.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .SingleAsync(s => s.Id == session.Id, Ct);
            if (ready)
                Assert.Equal(BiometricConsentStatus.Recorded, (await Flow().RecordConsentAsync(loaded, true, ServedVersion, IdentityChannels.AndroidChrome, Ct)).Status);

            return loaded;
        }

        /// <summary>The session as it was saved, read by another context on the same store.</summary>
        public async Task<GuestCheckInSession> ReadBackAsync(Guid sessionId)
        {
            await using var other = new AppDbContext(_options, null, _dataProtection);
            return await other.GuestCheckInSessions.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.Id == sessionId, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>The guests of the stay: the booker alone, as the real service answers before anyone is registered; the flow reads nothing else of the service.</summary>
    private sealed class Roster : IStayGuestService
    {
        public Task<IReadOnlyList<StayGuest>> GetForBookingAsync(Booking booking, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<StayGuest> bookerAlone = booking.Guest is { } booker ? new[] { StayGuest.FromBooker(booking, booker) } : Array.Empty<StayGuest>();
            return Task.FromResult(bookerAlone);
        }

        public Task<IReadOnlySet<int>> GetDocumentNumberPositionsAsync(Booking booking, IReadOnlyList<StayGuest> guests, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<StayGuest>>> GetForBookingsAsync(IReadOnlyCollection<Booking> bookings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(IReadOnlyList<StayGuestInput> guests, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(Booking booking, IReadOnlyList<StayGuestInput> guests, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StayGuestSaveResult> ReplaceAsync(
            Booking booking,
            IReadOnlyList<StayGuestInput> guests,
            StayGuestAuthor author,
            bool save = true,
            string? vehiclePlate = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>An agent that finds every capture good: the quality of the pictures is the business of the geometry here.</summary>
    private sealed class ScriptedAgent : IIdentityVerificationAgent
    {
        public Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct) =>
            Task.FromResult(new CaptureAssessment(true, [], [], true));

        public Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct) =>
            throw new NotSupportedException("The document step runs no verification.");
    }

    /// <summary>The alerts raised, in order.</summary>
    private sealed class RecordingAlerts : List<(Guid SessionId, string Reason)>, IVerificationOperatorAlerts
    {
        public Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
        {
            Add((sessionId, reasonCode));
            return Task.CompletedTask;
        }

        public Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>The MRZ of the pictures it was told about, by the SHA-256 of the bytes: the others have none (the front of a card has no MRZ).</summary>
    internal sealed class ScriptedMrz : IMrzExtractionService
    {
        private readonly Dictionary<string, MrzExtractionResult> _results = new(StringComparer.Ordinal);

        public void Add(byte[] picture, MrzExtractionResult result) => _results[Key(picture)] = result;

        public async Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            return (await ReadAsync(imageStream, cancellationToken)).Result;
        }

        public async Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await imageStream.CopyToAsync(copy, cancellationToken);
            return new MrzReading(_results.GetValueOrDefault(Key(copy.ToArray())), []);
        }

        private static string Key(byte[] picture) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(picture));
    }

    /// <summary>A reader of the visual zone that finds the evidence of a document in every picture: the reading of today accepts the front.</summary>
    internal sealed class EvidentDocumentOcr : IDocumentOcrService
    {
        public Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            Task.FromResult<DocumentOcrResult?>(new DocumentOcrResult { HasDocumentEvidence = true, Confidence = 0.55, Surname = Surname, GivenNames = GivenNames });
    }

    /// <summary>A reader of the zones with a fixed answer for every side: it records the card it was handed, and hands nothing of it on.</summary>
    internal sealed class ScriptedZonedReader : IZonedFieldReader
    {
        public Dictionary<DocumentSide, IReadOnlyList<ZonedField>> Fields { get; } = [];

        public List<(DocumentSide Side, int Width, int Height, bool PortraitIsWhereTheLayoutSaysIt, string FormatId)> Cards { get; } = [];

        public Task<IReadOnlyList<ZonedField>> ReadAsync(RasterImage canonicalCard, DocumentFormat format, DocumentSide side, CancellationToken cancellationToken = default)
        {
            // The middle of the zone of the portrait, below the dark bar of the stand-in: skin, when the card lies upright.
            var zone = format.Layout.Sides[DocumentSides.CodeOf(side)].Portrait;
            var portrait = zone is not null
                && DocumentImageFactory.IsSkin(
                    canonicalCard.Rgb,
                    ((((int)((zone.Y + (zone.H * 0.7)) * canonicalCard.Height)) * canonicalCard.Width) + (int)((zone.X + (zone.W * 0.5)) * canonicalCard.Width)) * 3);
            Cards.Add((side, canonicalCard.Width, canonicalCard.Height, portrait, format.Id));
            return Task.FromResult(Fields.GetValueOrDefault(side) ?? []);
        }
    }

    /// <summary>The face detector of the pictures of the factory, that can be made to fail, and counts the pictures it was asked about.</summary>
    internal sealed class SwitchableDetector : IFaceDetector
    {
        private readonly PortraitStandInDetector _inner = new();

        public Exception? Fail { get; set; }

        public int Calls => _inner.Calls;

        public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
        {
            if (Fail is not null)
                throw Fail;

            return _inner.Detect(image, minScore, cancellationToken);
        }
    }

    /// <summary>A private bucket in memory.</summary>
    private sealed class MemoryStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Objects[key] = copy.ToArray();
        }

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.ContainsKey(key));

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string key) => throw new NotSupportedException("The private bucket has no public URL.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(null);
    }

    /// <summary>A logger that keeps every formatted line, to prove what is not written.</summary>
    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception.GetType().Name));
    }
}
