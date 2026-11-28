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

        Assert.Equal(IdentityVerificationSteps.Presence, state.Step);
        Assert.Equal(["front", "back"], state.DocumentSides);
    }

    [Fact]
    public async Task DocumentStep_ADocumentWrittenBeforeTheFlowWasOn_IsNotCompleteForIt()
    {
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
        var nothing = PlainPicture(200);
        var maximum = world.Policies.Snapshot.Policy!.Document.MaxRecapturesPerSide;
        Assert.True(maximum > 0);

        for (var refusal = 1; refusal <= maximum; refusal++)
        {
            var refused = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(PlainPicture(100 + refusal)), DocumentSide.Front, Ct);
            Assert.Contains(IdentityGuidanceCodes.DocumentNotFound, refused.GuidanceCodes);
            Assert.DoesNotContain(IdentityGuidanceCodes.DocumentUnreadable, refused.GuidanceCodes);
            Assert.Equal(refusal, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals!["front"]);
        }

        Assert.Empty(world.Alerts);

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

    // ---- The count of the refusals: idempotent, atomic, and forgiven when the side is accepted -----------------------------------------------------------------------------

    [Fact]
    public async Task DocumentStep_TheSameRefusedFileSentAgain_IsRefusedAgainAndCountedOnce()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var cutByTheFrame = Picture(DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303));

        var first = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(cutByTheFrame), DocumentSide.Front, Ct);
        var again = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(cutByTheFrame), DocumentSide.Front, Ct);

        Assert.Equal(first.GuidanceCodes, again.GuidanceCodes);
        Assert.False(again.Acceptable);
        Assert.Equal(1, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals!["front"]);

        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(PlainPicture()), DocumentSide.Front, Ct);
        Assert.Equal(2, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals!["front"]);
        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(cutByTheFrame), DocumentSide.Front, Ct);
        Assert.Equal(3, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals!["front"]);
    }

    [Fact]
    public async Task DocumentStep_ARequestThatHoldsAnOlderRecord_DoesNotLoseTheRefusalSavedMeanwhile()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var (otherFlow, otherSession) = await world.AnotherRequestAsync(session.Id);

        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(PlainPicture(110)), DocumentSide.Front, Ct);
        await otherFlow.SubmitDocumentAsync(otherSession, new MemoryStream(PlainPicture(120)), DocumentSide.Front, Ct);

        Assert.Equal(2, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals!["front"]);
    }

    [Fact]
    public async Task DocumentStep_TwoPicturesAtTheSameTime_AreTwoRefusals()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var (otherFlow, otherSession) = await world.AnotherRequestAsync(session.Id);

        await Task.WhenAll(
            world.Flow().SubmitDocumentAsync(session, new MemoryStream(PlainPicture(110)), DocumentSide.Front, Ct),
            otherFlow.SubmitDocumentAsync(otherSession, new MemoryStream(PlainPicture(120)), DocumentSide.Front, Ct));

        Assert.Equal(2, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals!["front"]);
    }

    [Fact]
    public async Task DocumentStep_ASideThatIsAccepted_IsForgivenItsRefusals()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        var maximum = world.Policies.Snapshot.Policy!.Document.MaxRecapturesPerSide;

        for (var refusal = 1; refusal <= maximum; refusal++)
            await world.Flow().SubmitDocumentAsync(session, new MemoryStream(PlainPicture(100 + refusal)), DocumentSide.Front, Ct);

        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct)).DocumentStored);
        var accepted = ReadCheck(await world.ReadBackAsync(session.Id));
        Assert.Null(accepted.Refusals);
        Assert.Null(accepted.RefusedSha256);

        for (var refusal = 1; refusal <= maximum; refusal++)
            await world.Flow().SubmitDocumentAsync(session, new MemoryStream(PlainPicture(110 + refusal)), DocumentSide.Front, Ct);

        var saved = await world.ReadBackAsync(session.Id);
        Assert.Null(saved.IdentityClosedAt);
        Assert.Equal(maximum, ReadCheck(saved).Refusals!["front"]);
        Assert.Empty(world.Alerts);
    }

    [Fact]
    public async Task DocumentStep_TheSamePictureForTheOtherSide_IsADuplicate_AndAFrontSentAsTheBackIsTheWrongSide()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var front = Picture(DocumentSide.Front);
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(front), DocumentSide.Front, Ct)).DocumentStored);

        var duplicate = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(front), DocumentSide.Back, Ct);

        Assert.Equal(IdentityGuidanceCodes.DocumentSideDuplicate, duplicate.GuidanceCodes[0]);
        Assert.False(duplicate.Acceptable);
        Assert.Equal(["front"], duplicate.SidesReceived);

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
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Single, Ct);

        Assert.Equal([IdentityGuidanceCodes.DocumentWrongSide], result.GuidanceCodes);
        Assert.False(result.DocumentStored);
        Assert.Equal(new Dictionary<string, int> { ["single"] = 1 }, ReadCheck(await world.ReadBackAsync(session.Id)).Refusals);
    }

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
        world.Zoned.Fields[DocumentSide.Front] = FrontFields(printedSurname: "DIVERSOCOGNOME");
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct)).DocumentStored);

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

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

    [Fact]
    public async Task DocumentStep_TheZonesOfAPictureAreReadOnlyWhenTheCoherenceCanUseThem()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();

        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct);

        Assert.Empty(world.Zoned.Cards);

        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        Assert.Equal([DocumentSide.Back, DocumentSide.Front], world.Zoned.Cards.Select(card => card.Side));
        Assert.NotNull(ReadCheck(await world.ReadBackAsync(session.Id)).Coherence);
    }

    [Fact]
    public async Task DocumentStep_TheReaderOfTheZonesIsHandedTheCardCutOutAndUpright_AlsoWhenThePictureIsTurned()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct)).DocumentStored);
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var turned = DocumentImageFactory.TurnedCounterClockwise(upright);
        var turnedPicture = TestImages.Png(turned.Width, turned.Height, turned.Rgb);
        world.Zoned.Cards.Clear();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(turnedPicture), DocumentSide.Front, Ct);

        Assert.True(result.DocumentStored);
        var card = Assert.Single(world.Zoned.Cards, read => read.Side == DocumentSide.Front);
        Assert.Equal(DocumentSide.Front, card.Side);
        Assert.InRange(card.Width, 470, 495);
        Assert.InRange(card.Height, 295, 315);
        Assert.True(card.PortraitIsWhereTheLayoutSaysIt);
        Assert.Equal(Cie.Id, card.FormatId);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(-6.0)]
    [InlineData(8.0)]
    public async Task DocumentStep_TheReaderOfTheZonesIsHandedTheCardLevelled_WhenThePictureIsHeldCrooked(double degrees)
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct)).DocumentStored);
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, degrees);
        var crookedPicture = TestImages.Png(crooked.Width, crooked.Height, crooked.Rgb);
        world.Zoned.Cards.Clear();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(crookedPicture), DocumentSide.Front, Ct);

        Assert.True(result.DocumentStored);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentEdgesNotVisible, result.GuidanceCodes);
        var card = Assert.Single(world.Zoned.Cards, read => read.Side == DocumentSide.Front);
        Assert.InRange(card.Width, 470, 495);
        Assert.InRange(card.Height, 295, 315);
        Assert.True(card.PortraitIsWhereTheLayoutSaysIt);
        Assert.Equal(Cie.Id, card.FormatId);

        var saved = ReadCheck(await world.ReadBackAsync(session.Id));
        Assert.True(saved.Front!.GeometryOk);
        Assert.Equal("front", saved.Front.ClassifiedSide);
        foreach (var value in new[] { Surname, GivenNames, DocumentNumber, BirthDay, ExpiryDay })
            Assert.All(world.Logs, line => Assert.DoesNotContain(value, line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DocumentStep_ACardHeldMoreCrookedThanTheLimit_IsRefusedAsAnEdgeThatIsNotVisible_AndNothingOfItIsRead()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        using var upright = DocumentImageFactory.Render(Cie, DocumentSide.Front, DocumentImageFactory.WholeCard);
        using var crooked = DocumentImageFactory.Rotated(upright, 12);

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(TestImages.Png(crooked.Width, crooked.Height, crooked.Rgb)), DocumentSide.Front, Ct);

        Assert.False(result.Acceptable);
        Assert.Equal([IdentityGuidanceCodes.DocumentEdgesNotVisible], result.GuidanceCodes);
        Assert.False(result.DocumentStored);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(0, saved.BiometricAttempts);
        Assert.Equal(new Dictionary<string, int> { ["front"] = 1 }, ReadCheck(saved).Refusals);
        Assert.Empty(world.Zoned.Cards);
        Assert.Empty(world.Storage.Objects);
    }

    [Fact]
    public async Task DocumentStep_TheMrzOfAnUpload_IsReadOnItsCard_AndTheGeometryIsMadeOnce()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        Assert.Equal(DocumentReadingMethod.Mrz, result.Reading.Method);
        var read = Assert.Single(world.Mrz.Reads);
        Assert.InRange(read.Width, 470, 495);
        Assert.InRange(read.Height, 295, 315);
        Assert.Equal(DetectorCallsOfOneInspection(back, DocumentSide.Back), world.Detector.Calls);
    }

    [Fact]
    public async Task DocumentStep_APictureWithNoCardToStraighten_HasItsMrzReadAsItLies_AndIsAskedAgain()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var cutByTheFrame = Picture(DocumentSide.Back, new DocumentImageFactory.Placement(-100, 70, 480, 303));
        world.Mrz.Add(cutByTheFrame, CieMrz());

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(cutByTheFrame), DocumentSide.Back, Ct);

        Assert.Equal([(DocumentImageFactory.Width, DocumentImageFactory.Height)], world.Mrz.Reads);
        Assert.Equal([IdentityGuidanceCodes.DocumentEdgesNotVisible], result.GuidanceCodes);
        Assert.False(result.Acceptable);
        Assert.False(result.DocumentStored);
    }

    [Fact]
    public async Task DocumentStep_TheSideKeptOfTheOtherPicture_IsReadOnItsCardToo()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();

        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct);
        await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        // The front as it came, the back as it came, and the front again from its kept picture for the coherence of the two sides.
        Assert.Equal(3, world.Mrz.Reads.Count);
        Assert.All(world.Mrz.Reads, read =>
        {
            Assert.InRange(read.Width, 470, 495);
            Assert.InRange(read.Height, 295, 315);
        });
        Assert.NotNull(ReadCheck(await world.ReadBackAsync(session.Id)).Coherence);
    }

    [Fact]
    public async Task DocumentStep_ABackWhoseMrzIsNotRead_IsRefused_AndNeverPasses()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Back)), DocumentSide.Back, Ct);

        Assert.False(result.Acceptable);
        Assert.False(result.DocumentStored);
        Assert.Equal(DocumentReadingMethod.None, result.Reading.Method);
        Assert.Contains(CaptureGuidanceCodes.ShowWholeDocument, result.GuidanceCodes);
        Assert.Contains("document_not_recognized", result.GuidanceCodes);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(new Dictionary<string, int> { ["back"] = 1 }, ReadCheck(saved).Refusals);
        Assert.Null(saved.IdentityFormatId);
        Assert.Empty(world.Storage.Objects);
    }

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

    [Theory]
    [InlineData("unreadable", IdentityOutcomeCodes.Review)]
    [InlineData("unsupported", IdentityOutcomeCodes.FormatUnsupported)]
    [InlineData("technical", IdentityOutcomeCodes.TechnicalError)]
    [InlineData("unavailable", IdentityOutcomeCodes.Unavailable)]
    public async Task DocumentStep_EveryWayToAPerson_TakesTheSideAlreadyKept(string way, string outcome)
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        Assert.True((await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct)).DocumentStored);
        Assert.Single(world.Storage.Objects);
        Assert.Single(await world.Db.DocumentImages.IgnoreQueryFilters().Where(image => image.DeletedAt == null).ToListAsync(Ct));

        var back = Picture(DocumentSide.Back);
        switch (way)
        {
            case "unsupported":
                world.Mrz.Add(back, CieMrz(read =>
                {
                    read.DocumentCode = "P<";
                    read.MrzType = "TD3";
                }));
                break;
            case "technical":
                world.Mrz.Add(back, CieMrz());
                world.Detector.Fail = new BiometricUnavailableException();
                break;
            case "unavailable":
                world.Mrz.Add(back, CieMrz());
                world.Break("policy-missing");
                break;
        }

        if (way == "unreadable")
        {
            var maximum = world.Policies.Snapshot.Policy!.Document.MaxRecapturesPerSide;
            for (var refusal = 0; refusal <= maximum; refusal++)
                await world.Flow().SubmitDocumentAsync(session, new MemoryStream(PlainPicture(100 + refusal)), DocumentSide.Back, Ct);
        }
        else
        {
            await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);
        }

        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(outcome, saved.IdentityOutcomeCode);
        Assert.NotNull(saved.IdentityClosedAt);
        Assert.Empty(world.Storage.Objects);
        Assert.Empty(await world.Db.DocumentImages.IgnoreQueryFilters().Where(image => image.DeletedAt == null).ToListAsync(Ct));
    }

    [Fact]
    public async Task DocumentStep_ACaptureTheAgentFindsPoor_IsRefusedWithItsGuidance_AndIsNotKept()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        world.Agent.Codes = [CaptureGuidanceCodes.MoreLight, CaptureGuidanceCodes.AvoidGlare];

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct);

        Assert.False(result.Acceptable);
        Assert.Equal([CaptureGuidanceCodes.MoreLight, CaptureGuidanceCodes.AvoidGlare], result.GuidanceCodes);
        Assert.False(result.DocumentStored);
        Assert.Empty(world.Storage.Objects);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(0, saved.BiometricAttempts);
        Assert.Null(ReadCheck(saved).Front);
        Assert.Equal(1, ReadCheck(saved).Refusals!["front"]);
        Assert.Null(saved.OperatorRequestedAt);
    }

    [Fact]
    public async Task DocumentStep_APictureThatCannotBeDecoded_IsRefusedAsRetakePicture_AndCounted()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream("not a picture at all, only text"u8.ToArray()), DocumentSide.Front, Ct);

        Assert.False(result.Acceptable);
        Assert.Equal([CaptureGuidanceCodes.RetakePicture], result.GuidanceCodes);
        Assert.False(result.DocumentStored);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(0, saved.BiometricAttempts);
        Assert.Equal(1, ReadCheck(saved).Refusals!["front"]);
        Assert.Null(saved.OperatorRequestedAt);
        Assert.Empty(world.Storage.Objects);
    }

    [Fact]
    public async Task DocumentStep_ForAPropertyThatKeepsNoDocument_KeepsNothing_AndLeavesTheCoherenceUnknown()
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync(GivenNames, Surname, ready: true, propertyId: await world.SwissPropertyAsync());
        var back = Picture(DocumentSide.Back);
        world.Mrz.Add(back, CieMrz());
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();

        var first = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(Picture(DocumentSide.Front)), DocumentSide.Front, Ct);
        var second = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(back), DocumentSide.Back, Ct);

        Assert.True(first.DocumentNotArchived);
        Assert.False(first.DocumentStored);
        Assert.False(second.DocumentStored);
        Assert.True(second.Acceptable);
        Assert.Empty(world.Storage.Objects);
        Assert.Empty(await world.Db.DocumentImages.IgnoreQueryFilters().ToListAsync(Ct));
        var saved = await world.ReadBackAsync(session.Id);
        var check = ReadCheck(saved);
        Assert.True(check.Front!.GeometryOk);
        Assert.True(check.Back!.GeometryOk);
        Assert.Equal("it-cie-3.0", saved.IdentityFormatId);

        Assert.Null(check.Coherence);
        Assert.Empty(world.Zoned.Cards);
        Assert.True((await world.Flow().GetStateAsync(session, Ct)).DocumentChecked);
    }

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

        using var json = JsonDocument.Parse(saved.DocumentCheckJson!);
        foreach (var entry in json.RootElement.GetProperty("coherence").EnumerateArray())
            Assert.Equal(["check", "field", "result"], entry.EnumerateObject().Select(member => member.Name).Order());

        Assert.Equal(
            ["classifiedSide", "dHash", "face", "geometryOk", "mrz", "numberProof", "qualityOk"],
            json.RootElement.GetProperty("front").EnumerateObject().Select(member => member.Name).Order());
        Assert.NotEmpty(world.Logs);

        var passport = Picture(DocumentSide.Back, new DocumentImageFactory.Placement(90, 70, 480, 303));
        world.Mrz.Add(passport, CieMrz(read =>
        {
            read.DocumentType = "Passport";
            read.DocumentCode = "P<";
            read.MrzType = "TD3";
        }));

        var handedOff = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(passport), DocumentSide.Back, Ct);

        Assert.Equal([IdentityGuidanceCodes.DocumentFormatUnsupported], handedOff.GuidanceCodes);
        var closed = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityOutcomeCodes.FormatUnsupported, closed.IdentityOutcomeCode);
        var answer = JsonSerializer.Serialize(handedOff.GuidanceCodes, FlowJson);
        var state = JsonSerializer.Serialize(await world.Flow().GetStateAsync(session, Ct), FlowJson);
        Assert.NotEmpty(world.Alerts);
        foreach (var value in values)
        {
            Assert.DoesNotContain(value, closed.DocumentCheckJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, answer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(value, state, StringComparison.OrdinalIgnoreCase);
            Assert.All(world.Alerts, alert => Assert.DoesNotContain(value, alert.Reason, StringComparison.OrdinalIgnoreCase));
            Assert.All(world.Logs, line => Assert.DoesNotContain(value, line, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task DocumentStep_FlowOff_RunsNoneOfTheChecks_AndKeepsNoNewMember()
    {
        await using var world = await World.CreateAsync(FeatureFlags.CheckInIdentityVerification);
        var session = await world.ConsentedStayAsync();
        world.Zoned.Fields[DocumentSide.Front] = FrontFields();
        var picture = Picture(DocumentSide.Front, new DocumentImageFactory.Placement(-100, 70, 480, 303));

        var result = await world.Flow().SubmitDocumentAsync(session, new MemoryStream(picture), DocumentSide.Front, Ct);

        Assert.True(result.DocumentStored);
        Assert.Contains(CaptureGuidanceCodes.DocumentBackNeeded, result.GuidanceCodes);
        Assert.DoesNotContain(IdentityGuidanceCodes.DocumentEdgesNotVisible, result.GuidanceCodes);
        Assert.Equal(0, world.Detector.Calls);
        Assert.Equal([(DocumentImageFactory.Width, DocumentImageFactory.Height)], world.Mrz.Reads);
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

    private static int DetectorCallsOfOneInspection(byte[] picture, DocumentSide side)
    {
        var detector = new PortraitStandInDetector();
        var analyzer = new DocumentPictureAnalyzer(
            new DocumentGeometryInspector(detector, Options.Create(new BiometricModelOptions())), new ScriptedZonedReader(), Options.Create(new BiometricModelOptions()));
        using var opened = analyzer.Open(picture);
        opened.Inspect(Cie, side, Ct);
        return detector.Calls;
    }

    internal static byte[] PlainPicture(int level = 120)
    {
        using var image = DocumentImageFactory.Plain((byte)level);
        return TestImages.Png(image.Width, image.Height, image.Rgb);
    }

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

    private sealed class World : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"document-step-{Guid.NewGuid()}").Options;

        private static readonly IDataProtectionProvider SharedProtection = TestDataProtection.Shared;

        private readonly IDataProtectionProvider _dataProtection = SharedProtection;
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly Mock<ICheckInLegalTexts> _legalTexts = ActivePoseFlowTestVerifier.CreateRegistry();
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "casazen-document-step-" + Guid.NewGuid().ToString("N"));
        private readonly List<AppDbContext> _requests = [];
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

        public ScriptedAgent Agent { get; } = new();

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

        private static IdentityPolicyProvider ShippedPolicies(string pin = IdentityPolicyTestFiles.ShippedCanonicalHash) =>
            new(Options.Create(new IdentityVerificationOptions { PolicySha256 = pin }), NullLogger<IdentityPolicyProvider>.Instance, System.AppContext.BaseDirectory);

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

        public IdentityVerificationFlowService Flow(bool wired = true, AppDbContext? context = null)
        {
            var zoned = Zoned;
            var inspector = new DocumentGeometryInspector(Detector, Options.Create(new BiometricModelOptions()));
            return new IdentityVerificationFlowService(
                context ?? Db,
                Agent,
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

        public async Task<GuestCheckInSession> ConsentedStayAsync()
        {
            return await StayAsync(GivenNames, Surname, ready: true);
        }

        public async Task<Guid> SwissPropertyAsync()
        {
            var id = Guid.NewGuid();
            Db.Properties.Add(new Property
            {
                Id = id,
                OrgId = OrgId,
                OwnerId = "auth0|owner",
                Name = "Casa Ticino",
                Address = "Via Test 1",
                City = "Lugano",
                PostalCode = "6900",
                Country = "CH",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            });
            await Db.SaveChangesAsync(Ct);
            return id;
        }

        public async Task<GuestCheckInSession> StayAsync(string firstName, string lastName, bool ready = false, Guid? propertyId = null)
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
                PropertyId = propertyId ?? _propertyId,
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

        public async Task<GuestCheckInSession> ReadBackAsync(Guid sessionId)
        {
            await using var other = new AppDbContext(_options, null, _dataProtection);
            return await other.GuestCheckInSessions.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.Id == sessionId, Ct);
        }

        public async Task<(IdentityVerificationFlowService Flow, GuestCheckInSession Session)> AnotherRequestAsync(Guid sessionId)
        {
            var other = new AppDbContext(_options, null, _dataProtection);
            _requests.Add(other);
            var session = await other.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .SingleAsync(s => s.Id == sessionId, Ct);
            return (Flow(context: other), session);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var request in _requests)
                await request.DisposeAsync();

            await Db.DisposeAsync();
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
    }

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

    /// <summary>An agent that finds every capture good unless it is told otherwise: the quality of the pictures is the business of the geometry here.</summary>
    private sealed class ScriptedAgent : IIdentityVerificationAgent
    {
        public IReadOnlyList<string> Codes { get; set; } = [];

        public Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct) =>
            Task.FromResult(new CaptureAssessment(Codes.Count == 0, Codes, [], true));

        public Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct) =>
            throw new NotSupportedException("The document step runs no verification.");
    }

    private sealed class RecordingAlerts : List<(Guid SessionId, string Reason)>, IVerificationOperatorAlerts
    {
        public Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
        {
            Add((sessionId, reasonCode));
            return Task.CompletedTask;
        }

        public Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct) => Task.CompletedTask;
    }

    internal sealed class ScriptedMrz : IMrzExtractionService
    {
        private readonly Dictionary<string, MrzExtractionResult> _results = new(StringComparer.Ordinal);
        private MrzExtractionResult? _lastTold;

        public List<(int Width, int Height)> Reads { get; } = [];

        public void Add(byte[] picture, MrzExtractionResult result)
        {
            _results[Key(picture)] = result;
            _lastTold = result;
        }

        public async Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            return (await ReadAsync(imageStream, cancellationToken)).Result;
        }

        // The flow reads the MRZ on the card cut out of the upload, or on the upload itself when it has no card to straighten: a picture told about is known by its bytes, a card by the MRZ band it shows.
        public async Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await imageStream.CopyToAsync(copy, cancellationToken);
            var bytes = copy.ToArray();
            RasterImage picture;
            try
            {
                picture = RasterImage.Decode(bytes, 25_000_000);
            }
            catch (InvalidDataException)
            {
                return new MrzReading(_results.GetValueOrDefault(Key(bytes)), []);
            }

            using (picture)
            {
                Reads.Add((picture.Width, picture.Height));
                if (_results.TryGetValue(Key(bytes), out var told))
                    return new MrzReading(told, []);

                return new MrzReading(ShowsTheMrzBand(picture) ? _lastTold : null, []);
            }
        }

        private static string Key(byte[] picture) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(picture));

        private static bool ShowsTheMrzBand(RasterImage picture)
        {
            var ratio = (double)picture.Width / picture.Height;
            if (Math.Abs(ratio - Cie.Layout.AspectRatio) > Cie.Layout.AspectTolerance)
                return false;

            var band = Cie.Layout.Sides[DocumentSides.Back].MrzRegion!;
            var opposite = new DocumentFormatRegion(1 - band.X - band.W, 1 - band.Y - band.H, band.W, band.H);
            return DocumentGeometryInspectorTests.InkShare(picture, band) > 1.5 * DocumentGeometryInspectorTests.InkShare(picture, opposite);
        }
    }

    internal sealed class EvidentDocumentOcr : IDocumentOcrService
    {
        public Task<DocumentOcrResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            Task.FromResult<DocumentOcrResult?>(new DocumentOcrResult { HasDocumentEvidence = true, Confidence = 0.55, Surname = Surname, GivenNames = GivenNames });
    }

    internal sealed class ScriptedZonedReader : IZonedFieldReader
    {
        public Dictionary<DocumentSide, IReadOnlyList<ZonedField>> Fields { get; } = [];

        public List<(DocumentSide Side, int Width, int Height, bool PortraitIsWhereTheLayoutSaysIt, string FormatId)> Cards { get; } = [];

        public Task<IReadOnlyList<ZonedField>> ReadAsync(RasterImage canonicalCard, DocumentFormat format, DocumentSide side, CancellationToken cancellationToken = default)
        {
            var zone = format.Layout.Sides[DocumentSides.CodeOf(side)].Portrait;
            var portrait = zone is not null
                && DocumentImageFactory.IsSkin(
                    canonicalCard.Rgb,
                    ((((int)((zone.Y + (zone.H * 0.7)) * canonicalCard.Height)) * canonicalCard.Width) + (int)((zone.X + (zone.W * 0.5)) * canonicalCard.Width)) * 3);
            Cards.Add((side, canonicalCard.Width, canonicalCard.Height, portrait, format.Id));
            return Task.FromResult(Fields.GetValueOrDefault(side) ?? []);
        }
    }

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

    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception.GetType().Name));
    }
}
