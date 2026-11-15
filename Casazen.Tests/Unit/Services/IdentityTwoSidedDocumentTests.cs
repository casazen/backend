using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E22/F01/US01/T02: the identity document on two sides. The Italian electronic identity card and the other TD1 cards have the face on the front and the MRZ
/// on the back, and <c>CaptureQualityAssessor</c> used to accept a document only with both in the same picture, so every such guest ended at the operator.
/// The proofs: a card with the face on the front and the MRZ on the back reaches the selfie; both pictures follow the rules of one (encrypted until the
/// verification is over and then gone, none kept in Ticino); a side taken again is not a new attempt; and a single picture behaves as it always did.
/// The flow never decodes a picture (the agent, the MRZ reader and the OCR do), so the pictures of these tests are labelled bytes the fakes read.
/// </summary>
public class IdentityTwoSidedDocumentTests
{
    private const string LockCode = "7391";
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    // ─── The proofs of the brief ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): a TD1 card with the face on the front and the MRZ on the back reaches the selfie step. The front alone is not enough and says that the
    /// back is needed (and the selfie is refused, with no attempt spent); the back completes the document; the selfie is then compared with the FRONT, the
    /// picture with the face, and the MRZ read on the back is what the agent checks.
    /// </summary>
    [Fact]
    public async Task TwoSided_FaceFrontMrzBack_Acceptable_Test()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var front = Pic("FACE", 1);
        var back = Pic("MRZ", 2);

        var first = await world.DocumentAsync(session, front, DocumentSide.Front);
        Assert.False(first.Acceptable);
        Assert.Contains(CaptureGuidanceCodes.DocumentBackNeeded, first.GuidanceCodes);
        Assert.DoesNotContain(CaptureGuidanceCodes.ShowWholeDocument, first.GuidanceCodes);
        Assert.Equal(DocumentSide.Front, first.Side);
        Assert.Equal(new[] { "front" }, first.SidesReceived);
        var waiting = await world.Flow().GetStateAsync(session, CancellationToken.None);
        Assert.Equal(IdentityVerificationSteps.Document, waiting.Step);
        Assert.False(waiting.DocumentChecked);
        Assert.Equal(DocumentSides.Back, waiting.DocumentSideNeeded);

        // The selfie before the back: refused, nothing compared, no attempt spent.
        var early = await world.SelfieRawAsync(session);
        Assert.Equal(SelfieStepStatus.DocumentRequired, early.Status);
        Assert.Equal(0, session.BiometricAttempts);
        Assert.Equal(0, world.Agent.Calls);

        var second = await world.DocumentAsync(session, back, DocumentSide.Back);
        Assert.True(second.Acceptable);
        Assert.Empty(second.GuidanceCodes);
        Assert.Equal(DocumentSide.Back, second.Side);
        Assert.Equal(new[] { "front", "back" }, second.SidesReceived);
        Assert.Equal(DocumentReadingMethod.Mrz, second.Reading.Method);
        var ready = await world.Flow().GetStateAsync(session, CancellationToken.None);
        Assert.Equal(IdentityVerificationSteps.Selfie, ready.Step);
        Assert.True(ready.DocumentChecked);
        Assert.Null(ready.DocumentSideNeeded);
        Assert.Equal(new[] { "front", "back" }, ready.DocumentSides);

        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        var ok = await world.SelfieAsync(session);
        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        Assert.Equal(LockCode, ok.LockCode);
        Assert.Equal(front, world.Agent.LastDocumentPhoto);
        Assert.True(world.Agent.LastDocument!.MrzRead);
        Assert.True(world.Agent.LastDocument.CheckDigitsValid);
    }

    /// <summary>
    /// Proof (privacy): both pictures follow the rules of one. Between the document and the selfie they are in the private bucket, encrypted (never the
    /// bytes that were sent), in one row; and on every way out of the verification (a pass, a review, the last failure, an error, a refusal, a request for a
    /// person, a minor found on the back) both objects are gone and the row is marked deleted. So it is on the two ways that the administrator of the organization
    /// opens (E22/F01/US01/T03) while the guest is between the document and the selfie: the face check switched off, and the attempts lowered to the ones made.
    /// </summary>
    [Theory]
    [InlineData("ok")]
    [InlineData("review")]
    [InlineData("last-attempt")]
    [InlineData("error")]
    [InlineData("consent-refused")]
    [InlineData("request-operator")]
    [InlineData("switched-off-by-the-organization")]
    [InlineData("consent-after-switched-off")]
    [InlineData("attempts-lowered-by-the-organization")]
    public async Task TwoSided_NoImageKeptAfterVerification_Test(string exit)
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        var front = Pic("FACE", 1);
        var back = Pic("MRZ", 2);
        await world.DocumentAsync(session, front, DocumentSide.Front);
        var stored = await world.DocumentAsync(session, back, DocumentSide.Back);

        // Kept between the document and the selfie: two encrypted objects, one row holding both addresses.
        Assert.True(stored.DocumentStored);
        Assert.Equal(2, world.Storage.Objects.Count);
        Assert.All(world.Storage.Objects.Values, bytes =>
        {
            Assert.NotEqual(front, bytes);
            Assert.NotEqual(back, bytes);
            Assert.DoesNotContain("PIC|", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        });
        var row = await world.Db.DocumentImages.SingleAsync();
        Assert.False(string.IsNullOrWhiteSpace(row.FrontImageUrl));
        Assert.False(string.IsNullOrWhiteSpace(row.BackImageUrl));
        Assert.Null(row.DeletedAt);

        switch (exit)
        {
            case "ok":
                world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
                await world.SelfieAsync(session);
                break;
            case "review":
                world.Agent.Next(Outcome(IdentityVerificationResult.Review));
                await world.SelfieAsync(session);
                break;
            case "last-attempt":
                world.Agent.Next(Outcome(IdentityVerificationResult.NotOk, lastAttempt: true));
                await world.SelfieAsync(session);
                break;
            case "error":
                world.Agent.Fail(new InvalidOperationException("model crashed"));
                await world.SelfieAsync(session);
                break;
            case "consent-refused":
                await world.Flow().RecordConsentAsync(session, false, null, CancellationToken.None);
                break;
            case "request-operator":
                await world.Flow().RequestOperatorAsync(session, CancellationToken.None);
                break;
            case "switched-off-by-the-organization":
                await world.SetSettingsAsync(enabled: false, maxAttempts: 3);
                Assert.Equal(IdentityVerificationGuestResults.Operator, (await world.SelfieAsync(session)).Result);
                Assert.Equal(VerificationAlertReasons.BiometricsDisabled, Assert.Single(world.Alerts).Reason);
                break;
            case "consent-after-switched-off":
                await world.SetSettingsAsync(enabled: false, maxAttempts: 3);
                var consent = await world.Flow().RecordConsentAsync(session, true, "TEST-biometric-v1", CancellationToken.None);
                Assert.Equal(BiometricConsentStatus.Operator, consent.Status);
                Assert.Equal(VerificationAlertReasons.BiometricsDisabled, Assert.Single(world.Alerts).Reason);
                break;
            case "attempts-lowered-by-the-organization":
                // One attempt failed (the pictures stay for the next one), then the administrator lowers the attempts to the one that was made.
                world.Agent.Next(Outcome(IdentityVerificationResult.NotOk));
                Assert.Equal(IdentityVerificationGuestResults.Retry, (await world.SelfieAsync(session)).Result);
                Assert.Equal(2, world.Storage.Objects.Count);
                await world.SetSettingsAsync(enabled: true, maxAttempts: 1);
                Assert.Equal(IdentityVerificationGuestResults.Operator, (await world.SelfieAsync(session)).Result);
                Assert.Equal(VerificationAlertReasons.AttemptsExhausted, Assert.Single(world.Alerts).Reason);
                break;
        }

        Assert.Empty(world.Storage.Objects);
        Assert.All(await world.Db.DocumentImages.ToListAsync(), image => Assert.NotNull(image.DeletedAt));
        Assert.All(world.Logs, line =>
        {
            Assert.DoesNotContain("PIC|", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Anna", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Proof (privacy): a minor found on the BACK wipes the front that was already kept, and nothing of the back is kept: the operator takes over.</summary>
    [Fact]
    public async Task TwoSided_NoImageKeptAfterVerification_AMinorOnTheBack_WipesTheFront_Test()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        await world.DocumentAsync(session, Pic("FACE", 1), DocumentSide.Front);
        Assert.Single(world.Storage.Objects);

        var back = await world.DocumentAsync(session, Pic("MRZ-MINOR", 2), DocumentSide.Back);

        Assert.False(back.DocumentStored);
        Assert.Empty(world.Storage.Objects);
        Assert.All(await world.Db.DocumentImages.ToListAsync(), image => Assert.NotNull(image.DeletedAt));
        var selfie = await world.SelfieRawAsync(session);
        Assert.Equal(SelfieStepStatus.Done, selfie.Status);
        Assert.Equal(IdentityVerificationGuestResults.Operator, selfie.Result);
        Assert.Equal(0, world.Agent.Calls);
        Assert.Equal(VerificationAlertReasons.MinorExcluded, Assert.Single(world.Alerts).Reason);
    }

    /// <summary>
    /// The attempts are the organization's to change (E22/F01/US01/T03). A guest in the middle of his attempts finds the maximum lowered to the ones he made
    /// and no operator was told: the hand-off is made at his next step, once, so the alert, the "To do" and the queue of the operators exist (the next test:
    /// a hand-off that is open already is not told again).
    /// </summary>
    [Fact]
    public async Task TwoSided_AttemptsLoweredToTheOnesMade_HandOverToTheOperatorWithTheAlert_Once()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        await world.DocumentAsync(session, Pic("FACE", 1), DocumentSide.Front);
        await world.DocumentAsync(session, Pic("MRZ", 2), DocumentSide.Back);
        world.Agent.Next(Outcome(IdentityVerificationResult.NotOk));
        Assert.Equal(IdentityVerificationGuestResults.Retry, (await world.SelfieAsync(session)).Result);
        Assert.Null(session.OperatorRequestedAt);
        Assert.Empty(world.Alerts);

        await world.SetSettingsAsync(enabled: true, maxAttempts: 1);
        var handedOver = await world.SelfieAsync(session);

        Assert.Equal(IdentityVerificationGuestResults.Operator, handedOver.Result);
        Assert.Equal(0, handedOver.AttemptsRemaining);
        var alert = Assert.Single(world.Alerts);
        Assert.Equal(session.Id, alert.SessionId);
        Assert.Equal(VerificationAlertReasons.AttemptsExhausted, alert.Reason);
        Assert.NotNull(session.OperatorRequestedAt);
        Assert.True(VerificationOperatorRules.NeedsOperator.Compile()(session), "the queue of the operators lists the session");
        Assert.True(await VerificationHandOff.HasOpenHandOffAsync(world.Db, session.BookingId, CancellationToken.None));

        // The next step finds the hand-off open: no second alert, and the answer is the same.
        Assert.Equal(IdentityVerificationGuestResults.Operator, (await world.SelfieAsync(session)).Result);
        Assert.Single(world.Alerts);
        Assert.Equal(1, world.Agent.Calls);
    }

    /// <summary>A guest who asked for a person was told already: lowering the attempts later does not raise a second alert for his hand-off.</summary>
    [Fact]
    public async Task TwoSided_AttemptsLowered_AfterTheGuestAskedForAPerson_NoSecondAlert()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        await world.DocumentAsync(session, Pic("FACE", 1), DocumentSide.Front);
        await world.DocumentAsync(session, Pic("MRZ", 2), DocumentSide.Back);
        world.Agent.Next(Outcome(IdentityVerificationResult.NotOk));
        await world.SelfieAsync(session);
        await world.Flow().RequestOperatorAsync(session, CancellationToken.None);
        Assert.Equal(VerificationAlertReasons.GuestRequest, Assert.Single(world.Alerts).Reason);

        await world.SetSettingsAsync(enabled: true, maxAttempts: 1);
        var next = await world.SelfieAsync(session);

        Assert.Equal(IdentityVerificationGuestResults.Operator, next.Result);
        Assert.Single(world.Alerts);
    }

    /// <summary>
    /// Proof (privacy): a property in Ticino, or in Switzerland with a canton CasaZen does not know, keeps nothing of either side: no write to the storage,
    /// no row. The selfie then needs both pictures again, the ones that were read (by their SHA-256); the storage never sees one.
    /// </summary>
    [Fact]
    public async Task TwoSided_TicinoStoresNothing_Test()
    {
        await using var world = await World.CreateAsync();
        var propertyId = await world.AddPropertyAsync("CH");
        var session = await world.ConsentedStayAsync(propertyId);
        var front = Pic("FACE", 1);
        var back = Pic("MRZ", 2);

        var first = await world.DocumentAsync(session, front, DocumentSide.Front);
        var second = await world.DocumentAsync(session, back, DocumentSide.Back);

        Assert.True(first.DocumentNotArchived);
        Assert.False(first.DocumentStored);
        Assert.False(second.DocumentStored);
        Assert.True(second.Acceptable);
        Assert.Equal(0, world.Storage.Puts);
        Assert.Empty(world.Storage.Objects);
        Assert.Empty(await world.Db.DocumentImages.ToListAsync());

        // Without the pictures, with the front only, with another back, with another front: refused, the agent never runs.
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        Assert.Equal(SelfieStepStatus.DocumentPhotoRequired, (await world.SelfieRawAsync(session)).Status);
        Assert.Equal(SelfieStepStatus.DocumentPhotoRequired, (await world.SelfieRawAsync(session, front: front)).Status);
        Assert.Equal(SelfieStepStatus.DocumentPhotoMismatch, (await world.SelfieRawAsync(session, front: front, back: Pic("MRZ", 9))).Status);
        Assert.Equal(SelfieStepStatus.DocumentPhotoMismatch, (await world.SelfieRawAsync(session, front: Pic("FACE", 9), back: back)).Status);
        Assert.Equal(0, world.Agent.Calls);
        Assert.Equal(0, session.BiometricAttempts);

        // Both pictures that were read: the comparison runs on the front, and the storage has still never seen a picture.
        var ok = await world.SelfieAsync(session, front: front, back: back);
        Assert.Equal(IdentityVerificationGuestResults.Ok, ok.Result);
        Assert.Equal(LockCode, ok.LockCode);
        Assert.Equal(front, world.Agent.LastDocumentPhoto);
        Assert.Equal(0, world.Storage.Puts);
        Assert.Empty(await world.Db.DocumentImages.ToListAsync());
    }

    /// <summary>Proof (privacy): the erasure of a guest and the receipt of the communication delete both pictures of a two-sided document (the cleaner reads both addresses).</summary>
    [Fact]
    public async Task TwoSided_ErasureDeletesBothPictures_Test()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        await world.DocumentAsync(session, Pic("FACE", 1), DocumentSide.Front);
        await world.DocumentAsync(session, Pic("MRZ", 2), DocumentSide.Back);
        Assert.Equal(2, world.Storage.Objects.Count);

        var images = await world.Db.DocumentImages.IgnoreQueryFilters().ToListAsync();
        var deleted = await DocumentImageCleaner.DeleteAsync(world.Db, world.Storage, NullLogger.Instance, images, CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Empty(world.Storage.Objects);
    }

    // ─── The rules of the attempts and of what each side may do ──────────────────────────────────────────

    /// <summary>A side taken again is not a new attempt, and it replaces only its own picture: the attempts count the complete document and selfie tries.</summary>
    [Fact]
    public async Task TwoSided_RetakingASide_IsNotANewAttempt_AndReplacesOnlyItsOwnPicture()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        await world.DocumentAsync(session, Pic("FACE", 1), DocumentSide.Front);
        await world.DocumentAsync(session, Pic("MRZ", 2), DocumentSide.Back);
        world.Agent.Next(Outcome(IdentityVerificationResult.NotOk));
        Assert.Equal(IdentityVerificationGuestResults.Retry, (await world.SelfieAsync(session)).Result);
        Assert.Equal(1, session.BiometricAttempts);
        var rowBefore = await world.Db.DocumentImages.SingleAsync();
        var frontKey = rowBefore.FrontImageUrl;
        var backKey = rowBefore.BackImageUrl;

        // The back is taken again, then the front: no attempt is added, each picture is replaced and the other stays.
        var newBack = Pic("MRZ", 3);
        await world.DocumentAsync(session, newBack, DocumentSide.Back);
        var row = await world.Db.DocumentImages.SingleAsync();
        Assert.Equal(1, session.BiometricAttempts);
        Assert.Equal(frontKey, row.FrontImageUrl);
        Assert.NotEqual(backKey, row.BackImageUrl);
        Assert.Equal(2, world.Storage.Objects.Count);

        var newFront = Pic("FACE", 4);
        var third = await world.DocumentAsync(session, newFront, DocumentSide.Front);
        Assert.True(third.Acceptable);
        Assert.Equal(1, session.BiometricAttempts);
        Assert.Equal(2, world.Storage.Objects.Count);
        Assert.Single(await world.Db.DocumentImages.ToListAsync());

        // The second attempt compares the NEW front.
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        Assert.Equal(IdentityVerificationGuestResults.Ok, (await world.SelfieAsync(session)).Result);
        Assert.Equal(newFront, world.Agent.LastDocumentPhoto);
        Assert.Equal(2, session.BiometricAttempts);
    }

    /// <summary>The MRZ may come from any side: a card that has it on the front needs no back.</summary>
    [Fact]
    public async Task TwoSided_MrzOnTheFront_NeedsNoBack()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var front = await world.DocumentAsync(session, Pic("FACE-MRZ", 1), DocumentSide.Front);

        Assert.True(front.Acceptable);
        Assert.Empty(front.GuidanceCodes);
        var state = await world.Flow().GetStateAsync(session, CancellationToken.None);
        Assert.Equal(IdentityVerificationSteps.Selfie, state.Step);
        Assert.Null(state.DocumentSideNeeded);
    }

    /// <summary>The back first: the MRZ is read, the face is missing, so the front is asked for; the selfie waits for it.</summary>
    [Fact]
    public async Task TwoSided_TheBackFirst_AsksForTheFront()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var back = await world.DocumentAsync(session, Pic("MRZ", 1), DocumentSide.Back);

        Assert.False(back.Acceptable);
        Assert.Contains(CaptureGuidanceCodes.DocumentFrontNeeded, back.GuidanceCodes);
        Assert.Equal(new[] { "back" }, back.SidesReceived);
        var state = await world.Flow().GetStateAsync(session, CancellationToken.None);
        Assert.Equal(IdentityVerificationSteps.Document, state.Step);
        Assert.Equal(DocumentSides.Front, state.DocumentSideNeeded);
        Assert.Equal(SelfieStepStatus.DocumentRequired, (await world.SelfieRawAsync(session)).Status);

        // The back is kept (encrypted) while the front is awaited; the row has no front yet, and the front completes it.
        var row = await world.Db.DocumentImages.SingleAsync();
        Assert.True(string.IsNullOrWhiteSpace(row.FrontImageUrl));
        Assert.False(string.IsNullOrWhiteSpace(row.BackImageUrl));

        var front = await world.DocumentAsync(session, Pic("FACE", 2), DocumentSide.Front);
        Assert.True(front.Acceptable);
        Assert.Equal(new[] { "front", "back" }, front.SidesReceived);
        Assert.Equal(2, world.Storage.Objects.Count);
        world.Agent.Next(Outcome(IdentityVerificationResult.Ok));
        Assert.Equal(IdentityVerificationGuestResults.Ok, (await world.SelfieAsync(session)).Result);
        Assert.Empty(world.Storage.Objects);
    }

    /// <summary>
    /// A back that is not readable says so with the guidance it always had, and a back that was read is not asked for the quality of a photo without a face: the
    /// MRZ read with valid check digits is the proof the picture was good enough.
    /// </summary>
    [Fact]
    public async Task TwoSided_TheBack_IsJudgedOnItsMrz_NotOnAFace()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        await world.DocumentAsync(session, Pic("FACE", 1), DocumentSide.Front);

        var unreadable = await world.DocumentAsync(session, Pic("BLANK-BLUR", 2), DocumentSide.Back);
        Assert.False(unreadable.Acceptable);
        Assert.Contains(CaptureGuidanceCodes.ShowWholeDocument, unreadable.GuidanceCodes);
        Assert.Contains(CaptureGuidanceCodes.HoldStill, unreadable.GuidanceCodes);
        Assert.DoesNotContain(CaptureGuidanceCodes.ShowDocumentPhoto, unreadable.GuidanceCodes);
        Assert.Equal(DocumentSides.Back, (await world.Flow().GetStateAsync(session, CancellationToken.None)).DocumentSideNeeded);

        var readable = await world.DocumentAsync(session, Pic("MRZ-BLUR", 3), DocumentSide.Back);
        Assert.True(readable.Acceptable);
    }

    /// <summary>A front with no face is asked again with the usual guidance, and does not ask for a back.</summary>
    [Fact]
    public async Task TwoSided_AFrontWithoutAFace_IsAskedAgain_NotForTheBack()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var front = await world.DocumentAsync(session, Pic("BLANK", 1), DocumentSide.Front);

        Assert.False(front.Acceptable);
        Assert.Contains(CaptureGuidanceCodes.ShowDocumentPhoto, front.GuidanceCodes);
        Assert.DoesNotContain(CaptureGuidanceCodes.DocumentBackNeeded, front.GuidanceCodes);
        Assert.Equal(DocumentSides.Front, (await world.Flow().GetStateAsync(session, CancellationToken.None)).DocumentSideNeeded);
    }

    /// <summary>
    /// The default is today's behaviour: one picture with the face and the MRZ is acceptable; a picture with no MRZ says «show the whole document» and never asks
    /// for a back; the step moves on to the selfie whatever the picture said (the agent says what is wrong there); a single picture replaces everything
    /// kept before, and a two-sided attempt can start over with it.
    /// </summary>
    [Fact]
    public async Task SinglePicture_KeepsTodaysBehaviour()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();

        var passport = await world.DocumentAsync(session, Pic("FACE-MRZ", 1), DocumentSide.Single);
        Assert.True(passport.Acceptable);
        Assert.Empty(passport.GuidanceCodes);
        Assert.Equal(DocumentSide.Single, passport.Side);
        Assert.Equal(new[] { "single" }, passport.SidesReceived);

        var noMrz = await world.DocumentAsync(session, Pic("FACE", 2));
        Assert.False(noMrz.Acceptable);
        Assert.Contains(CaptureGuidanceCodes.ShowWholeDocument, noMrz.GuidanceCodes);
        Assert.DoesNotContain(CaptureGuidanceCodes.DocumentBackNeeded, noMrz.GuidanceCodes);
        var state = await world.Flow().GetStateAsync(session, CancellationToken.None);
        Assert.Equal(IdentityVerificationSteps.Selfie, state.Step);
        Assert.True(state.DocumentChecked);
        Assert.Null(state.DocumentSideNeeded);
        Assert.Equal(new[] { "single" }, state.DocumentSides);

        // A two-sided attempt, then a single picture: both pictures are replaced by it.
        await world.DocumentAsync(session, Pic("FACE", 3), DocumentSide.Front);
        await world.DocumentAsync(session, Pic("MRZ", 4), DocumentSide.Back);
        Assert.Equal(2, world.Storage.Objects.Count);
        await world.DocumentAsync(session, Pic("FACE-MRZ", 5), DocumentSide.Single);
        Assert.Single(world.Storage.Objects);
        var row = Assert.Single(await world.Db.DocumentImages.Where(i => i.DeletedAt == null).ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(row.FrontImageUrl));
        Assert.True(string.IsNullOrWhiteSpace(row.BackImageUrl));
    }

    /// <summary>A record the session kept before the two-sided document (no sides in it) reads as a single picture: nothing changes for a session in flight.</summary>
    [Fact]
    public async Task ARecordWrittenBeforeTheTwoSidedDocument_ReadsAsASinglePicture()
    {
        await using var world = await World.CreateAsync();
        var session = await world.ConsentedStayAsync();
        session.DocumentCheckJson =
            """{"check":{"mrzRead":true,"checkDigitsValid":true,"expired":false,"namesConsistent":true,"birthDateConsistent":true,"documentNumberConsistent":true,"nationalityConsistent":true,"holderIsMinor":false},"photoSha256":"ABC","checkedAt":"2026-11-13T10:00:00Z"}""";

        var state = await world.Flow().GetStateAsync(session, CancellationToken.None);

        Assert.Equal(IdentityVerificationSteps.Selfie, state.Step);
        Assert.True(state.DocumentChecked);
        Assert.Equal(new[] { "single" }, state.DocumentSides);
        Assert.Null(state.DocumentSideNeeded);
    }

    // ─── The assessor and the endpoint ───────────────────────────────────────────────────────────────────

    /// <summary>The back of a card is not asked for a face: the detector is not called, and the answer does not depend on the models being there.</summary>
    [Fact]
    public void Assessor_TheBackOfACard_IsNotAskedForAFace()
    {
        var picture = TestImages.Png(96, 64, seed: 21);
        var options = Options.Create(new BiometricModelOptions());

        // A document picture with no face: the photo of the document is missing.
        var noFace = new ScriptedFaceDetector(Array.Empty<DetectedFace>());
        using (var image = RasterImage.Decode(picture, options.Value.MaxImagePixels))
        {
            var front = new CaptureQualityAssessor(noFace, options).Assess(image, CaptureKind.Document, CancellationToken.None);
            Assert.Contains(CaptureGuidanceCodes.ShowDocumentPhoto, front.GuidanceCodes);
            Assert.Equal(1, noFace.Calls);
        }

        // The back of a card: no face is looked for, even with models that cannot run.
        var broken = new ScriptedFaceDetector { Failure = new BiometricUnavailableException() };
        using (var image = RasterImage.Decode(picture, options.Value.MaxImagePixels))
        {
            var back = new CaptureQualityAssessor(broken, options).Assess(image, CaptureKind.DocumentBack, CancellationToken.None);
            Assert.DoesNotContain(CaptureGuidanceCodes.ShowDocumentPhoto, back.GuidanceCodes);
            Assert.True(back.Assessed);
            Assert.Equal(0, broken.Calls);
        }
    }

    [Theory]
    [InlineData(null, DocumentSide.Single)]
    [InlineData("", DocumentSide.Single)]
    [InlineData("  ", DocumentSide.Single)]
    [InlineData("single", DocumentSide.Single)]
    [InlineData("front", DocumentSide.Front)]
    [InlineData("BACK", DocumentSide.Back)]
    [InlineData(" Front ", DocumentSide.Front)]
    public void Sides_TheEndpointParsesTheSide_AndNothingIsSingle(string? value, DocumentSide expected)
    {
        Assert.True(DocumentSides.TryParse(value, out var side));
        Assert.Equal(expected, side);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("both")]
    [InlineData("1")]
    public void Sides_AnythingElse_IsNotASide(string value)
    {
        Assert.False(DocumentSides.TryParse(value, out _));
    }

    [Fact]
    public async Task Endpoint_AnInvalidSide_Answers400AndTheFlowNeverSeesThePicture()
    {
        var flow = new Mock<IIdentityVerificationFlowService>();
        var controller = Controller(flow);

        var result = await controller.UploadDocument("token", Image("document"), "left");

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal("document_side_invalid", Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
        flow.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, DocumentSide.Single)]
    [InlineData("front", DocumentSide.Front)]
    [InlineData("back", DocumentSide.Back)]
    public async Task Endpoint_TheSideReachesTheFlow_AndTheAnswerSaysWhichSideWasRead(string? side, DocumentSide expected)
    {
        var flow = new Mock<IIdentityVerificationFlowService>();
        flow.Setup(f => f.SubmitDocumentAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), expected, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentStepResult(
                DocumentReading.Nothing,
                false,
                [CaptureGuidanceCodes.DocumentBackNeeded],
                true,
                false,
                expected,
                new[] { DocumentSides.CodeOf(expected) }));

        var result = await Controller(flow).UploadDocument("token", Image("document"), side);

        var response = Assert.IsType<DocumentUploadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(DocumentSides.CodeOf(expected), response.Side);
        Assert.Equal(new[] { DocumentSides.CodeOf(expected) }, response.Sides);
        Assert.Equal(new[] { CaptureGuidanceCodes.DocumentBackNeeded }, response.GuidanceCodes);
    }

    [Fact]
    public async Task Endpoint_TheBackPhotoOfTheSelfieStep_ReachesTheFlow()
    {
        var flow = new Mock<IIdentityVerificationFlowService>();
        Stream? seenBack = null;
        flow.Setup(f => f.SubmitSelfieAsync(It.IsAny<GuestCheckInSession>(), It.IsAny<Stream>(), It.IsAny<Stream?>(), It.IsAny<Stream?>(), It.IsAny<CancellationToken>()))
            .Callback<GuestCheckInSession, Stream, Stream?, Stream?, CancellationToken>((_, _, _, back, _) => seenBack = back)
            .ReturnsAsync(new SelfieStepResult(SelfieStepStatus.Done, IdentityVerificationGuestResults.Retry, [], 3, null));

        var result = await Controller(flow).UploadSelfie("token", Image("selfie"), Image("documentPhoto"), Image("documentPhotoBack"));

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(seenBack);

        // A back that is not a picture is refused before the flow sees anything.
        var refused = await Controller(new Mock<IIdentityVerificationFlowService>()).UploadSelfie("token", Image("selfie"), null, Image("documentPhotoBack", "text/plain"));
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(refused.Result).StatusCode);
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A picture the fakes read: its traits say what is on it (<c>FACE</c>, <c>MRZ</c>, <c>MINOR</c> for an MRZ of a minor, <c>BLUR</c>), the number makes its bytes unique.</summary>
    private static byte[] Pic(string traits, int number) => Encoding.UTF8.GetBytes($"PIC|{traits}|{number}|" + new string('#', 48));

    private static IdentityVerificationOutcome Outcome(IdentityVerificationResult result, bool lastAttempt = false) => new(
        result,
        result == IdentityVerificationResult.Ok
            ? [new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Passed, 0.93, 0.85)]
            : [new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Failed, 0.31, 0.70)],
        result == IdentityVerificationResult.Ok ? [] : [CaptureGuidanceCodes.RetakeSelfie],
        lastAttempt,
        "AuraFace-v1",
        "1.0.0",
        new VerificationThresholds(0.85, 0.70, 0.7, 3, CaptureQualityRules.Version, Provisional: true),
        840);

    private static FormFile Image(string field, string contentType = "image/png")
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, $"{field}.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType,
        };
    }

    private static PublicGuestCheckInController Controller(Mock<IIdentityVerificationFlowService> flow)
    {
        var checkIn = new Mock<IGuestCheckInService>();
        checkIn.Setup(s => s.GetSessionByTokenAsync("token"))
            .ReturnsAsync(new GuestCheckInSession { Id = Guid.NewGuid(), BookingId = Guid.NewGuid(), OrgId = Guid.NewGuid() });

        return new PublicGuestCheckInController(
            checkIn.Object,
            Mock.Of<IAlloggiatiCodeTableService>(),
            Mock.Of<ICheckInDocumentReader>(),
            Mock.Of<IAlloggiatiReportScheduler>(),
            flow.Object,
            Mock.Of<IFeatureFlags>(),
            Mock.Of<IStringLocalizer<SharedResources>>(),
            NullLogger<PublicGuestCheckInController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    /// <summary>One org on the Agency plan (the biometric verification on by default), a property in Italy, the flow and its fakes.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"two-sided-document-{Guid.NewGuid()}").Options;

        private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly Mock<IEmailQueue> _queue = new();
        private readonly Mock<IAccessCodeService> _codes = new();

        private World()
        {
            Db = new AppDbContext(_options, null, _dataProtection);
            _queue.Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>())).Returns(true);
            _codes.Setup(c => c.ReleaseToGuestAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(LockCode);
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public Casazen.Tests.Unit.FakeTimeProvider Clock { get; } = new(Now);

        public RecordingAgent Agent { get; } = new();

        public RecordingAlerts Alerts { get; } = new();

        public PictureMrz Mrz { get; } = new();

        public MemoryStorage Storage { get; } = new();

        public List<string> Logs { get; } = [];

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            world.Db.Orgs.Add(new OrgEntity
            {
                Id = world.OrgId,
                Name = "Agenzia documenti",
                Slug = $"documenti-{world.OrgId:N}"[..20],
                DisplayName = "Agenzia documenti",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Agency,
            });
            world.Db.Properties.Add(world.NewProperty(world._propertyId, "IT"));
            await world.Db.SaveChangesAsync();
            return world;
        }

        public IdentityVerificationFlowService Flow() => new(
            Db,
            Agent,
            Alerts,
            _codes.Object,
            _queue.Object,
            Mrz,
            Mock.Of<IDocumentOcrService>(),
            new StayGuestService(Db, Mock.Of<IAlloggiatiCodeTableService>()),
            Storage,
            _dataProtection,
            Clock,
            new ListLogger<IdentityVerificationFlowService>(Logs));

        /// <summary>A property in another country: Switzerland without a canton CasaZen knows keeps no document, like Ticino.</summary>
        public async Task<Guid> AddPropertyAsync(string country)
        {
            var property = NewProperty(Guid.NewGuid(), country);
            Db.Properties.Add(property);
            await Db.SaveChangesAsync();
            return property.Id;
        }

        /// <summary>A confirmed stay of an adult booker with an open session whose guest gave the biometric consent (loaded as the public endpoint loads it).</summary>
        public async Task<GuestCheckInSession> ConsentedStayAsync(Guid? propertyId = null)
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                FirstName = "Anna",
                LastName = "Bianchi",
                Email = "anna@example.com",
                DateOfBirth = new DateTime(1990, 1, 1),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                PropertyId = propertyId ?? _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 20),
                CheckOutDate = new DateTime(2026, 11, 22),
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
                ExpiresAt = Now.UtcDateTime.AddDays(10),
                Status = GuestCheckInSessionStatus.InCompilazione,
                Language = "it",
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            await Db.SaveChangesAsync();

            var loaded = await Db.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .SingleAsync(s => s.Id == session.Id);
            var consent = await Flow().RecordConsentAsync(loaded, true, "TEST-biometric-v1", CancellationToken.None);
            Assert.Equal(BiometricConsentStatus.Recorded, consent.Status);
            return loaded;
        }

        /// <summary>The row of the settings of the org as an administrator saves it through <c>api/settings/biometrics</c>: the flow reads it at its next step.</summary>
        public async Task SetSettingsAsync(bool enabled, int maxAttempts)
        {
            var row = await Db.BiometricSettings.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.OrgId == OrgId);
            if (row is null)
            {
                row = new BiometricSettings { OrgId = OrgId, AcceptThreshold = 0.85m, EscalateThreshold = 0.70m, CreatedAt = Now.UtcDateTime };
                Db.BiometricSettings.Add(row);
            }

            row.Enabled = enabled;
            row.MaxAttempts = maxAttempts;
            row.UpdatedAt = Now.UtcDateTime;
            await Db.SaveChangesAsync();
        }

        public async Task<DocumentStepResult> DocumentAsync(GuestCheckInSession session, byte[] picture, DocumentSide side = DocumentSide.Single)
        {
            using var stream = new MemoryStream(picture);
            return await Flow().SubmitDocumentAsync(session, stream, side, CancellationToken.None);
        }

        public async Task<SelfieStepResult> SelfieRawAsync(GuestCheckInSession session, byte[]? front = null, byte[]? back = null)
        {
            using var selfie = new MemoryStream(Pic("SELFIE", 0));
            using var frontStream = front is null ? null : new MemoryStream(front);
            using var backStream = back is null ? null : new MemoryStream(back);
            return await Flow().SubmitSelfieAsync(session, selfie, frontStream, backStream, CancellationToken.None);
        }

        public async Task<SelfieStepResult> SelfieAsync(GuestCheckInSession session, byte[]? front = null, byte[]? back = null)
        {
            var result = await SelfieRawAsync(session, front, back);
            Assert.Equal(SelfieStepStatus.Done, result.Status);
            return result;
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();

        private Property NewProperty(Guid id, string country) => new()
        {
            Id = id,
            OrgId = OrgId,
            OwnerId = "auth0|owner",
            Name = "Casa Test",
            Address = "Via Test 1",
            City = country == "CH" ? "Lugano" : "Roma",
            PostalCode = "00100",
            Country = country,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 100m,
        };
    }

    /// <summary>
    /// An agent that reads the traits of a picture (a face on it, light and blur) the way the real one reports them, answers a script for the decision, and
    /// keeps what reached it (the photo of the document it compared and the checks of the document).
    /// </summary>
    private sealed class RecordingAgent : IIdentityVerificationAgent
    {
        private readonly Queue<Func<IdentityVerificationOutcome>> _answers = new();

        public byte[]? LastDocumentPhoto { get; private set; }

        public DocumentCheck? LastDocument { get; private set; }

        public int Calls { get; private set; }

        public void Next(IdentityVerificationOutcome outcome) => _answers.Enqueue(() => outcome);

        public void Fail(Exception error) => _answers.Enqueue(() => throw error);

        public async Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct)
        {
            var label = Encoding.UTF8.GetString(await ReadAsync(image, ct));
            var guidance = new List<string>();
            if (label.Contains("BLUR", StringComparison.Ordinal))
                guidance.Add(CaptureGuidanceCodes.HoldStill);

            // The face of the document is looked for on the front and on a single picture, never on the back of a card.
            if (kind == CaptureKind.Document && !label.Contains("FACE", StringComparison.Ordinal))
                guidance.Add(CaptureGuidanceCodes.ShowDocumentPhoto);

            return new CaptureAssessment(guidance.Count == 0, guidance, [], true);
        }

        public async Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct)
        {
            Calls++;
            LastDocumentPhoto = await ReadAsync(request.DocumentPhoto, ct);
            LastDocument = request.Document;
            await ReadAsync(request.Selfie, ct);
            return _answers.Dequeue()();
        }

        private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            return copy.ToArray();
        }
    }

    /// <summary>An MRZ reader that finds an MRZ on the pictures labelled <c>MRZ</c> (of a minor when labelled <c>MINOR</c>) and none on the others.</summary>
    private sealed class PictureMrz : IMrzExtractionService
    {
        public Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default) =>
            Task.FromResult(Read(imageStream));

        public Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            var result = Read(imageStream);
            return Task.FromResult(new MrzReading(result, result is null ? new[] { MrzQualityCodes.MrzNotFound } : Array.Empty<string>()));
        }

        private static MrzExtractionResult? Read(Stream stream)
        {
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var label = Encoding.UTF8.GetString(copy.ToArray());
            if (!label.Contains("MRZ", StringComparison.Ordinal))
                return null;

            return new MrzExtractionResult
            {
                DocumentType = "IdCard",
                Surname = "TEST",
                GivenNames = "TEST",
                DocumentNumber = "CA00000AA",
                DateOfBirth = label.Contains("MINOR", StringComparison.Ordinal) ? new DateTime(2014, 5, 5) : new DateTime(1990, 1, 1),
                ExpirationDate = new DateTime(2030, 1, 1),
                IsExpired = false,
                Nationality = "ITA",
                CheckDigitsValid = true,
                Confidence = 0.95,
            };
        }
    }

    /// <summary>The alerts raised, in order, and the "To do" items closed.</summary>
    private sealed class RecordingAlerts : List<(Guid SessionId, string Reason)>, IVerificationOperatorAlerts
    {
        public Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
        {
            Add((sessionId, reasonCode));
            return Task.CompletedTask;
        }

        public Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A private bucket in memory, counting the writes.</summary>
    private sealed class MemoryStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public int Puts { get; private set; }

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            Puts++;
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

    /// <summary>A logger that keeps every formatted line, to prove what is (not) written.</summary>
    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
