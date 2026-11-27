using System.Reflection;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The rules of the compliance traffic light (E03/F02/US01/T03, NF-52, NF-50): green only with an archived proof, yellow for the work in hand
/// (entrusted to the channel manager, sent and awaiting the receipt, the period open), red for what the host has to do or what a deadline made
/// late; and where the outcome is held only by Octorate, never an inferred success or failure.
/// </summary>
public class ComplianceLightRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 6);
    private static readonly ComplianceProof Receipt = new("manual-proof:1", ComplianceEvidenceSources.ManualUpload, Now.AddHours(-3));

    private static AlloggiatiFacts Alloggiati(
        ComplianceSenderType sender = ComplianceSenderType.Manual,
        AlloggiatiWebStatus status = AlloggiatiWebStatus.DaInviareManualmente,
        int nights = 2,
        double deadlineInHours = 10,
        bool entrusted = false,
        DateTime? communicatedAt = null,
        ConnectionHealthState? connection = null,
        ComplianceProof? proof = null) =>
        new()
        {
            Sender = sender,
            Status = status,
            StayNights = nights,
            DeadlineUtc = Now.AddHours(deadlineInHours),
            NowUtc = Now,
            TodayRome = Today,
            GuestsEntrusted = entrusted,
            CommunicatedAt = communicatedAt,
            Connection = connection,
            Proof = proof,
        };

    [Fact]
    public void ComplianceStatus_NeverGreenWithoutArchivedProof_Test()
    {
        var constructors = typeof(ComplianceVerdict).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotEmpty(constructors);
        Assert.All(constructors, c => Assert.False(c.IsPublic));
        var factories = typeof(ComplianceVerdict)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(ComplianceVerdict));
        Assert.All(factories, m => Assert.False(m.IsPublic));
        Assert.Throws<ArgumentException>(() => new ComplianceProof(" ", "manual_upload"));
        Assert.Throws<ArgumentException>(() => new ComplianceProof("manual-proof:1", ""));

        var verdicts = new List<ComplianceVerdict>();

        // Alloggiati, every combination of what the rule looks at, with no proof at all.
        var statuses = Enum.GetValues<AlloggiatiWebStatus>();
        var alloggiatiFacts =
            from sender in Enum.GetValues<ComplianceSenderType>()
            from status in statuses
            from nights in new[] { 0, 1, 2, 31 }
            from entrusted in new[] { false, true }
            from connection in new ConnectionHealthState?[] { null, ConnectionHealthState.Active, ConnectionHealthState.Inactive, ConnectionHealthState.Unknown, ConnectionHealthState.Manual }
            from hours in new double[] { -30, -1, 1, 30 }
            from communicatedAt in new DateTime?[] { null, Now, Now.AddDays(-2) }
            select Alloggiati(sender, status, nights, hours, entrusted, communicatedAt, connection);
        foreach (var facts in alloggiatiFacts)
        {
            var verdict = ComplianceLightRules.Alloggiati(facts).Verdict;
            Assert.NotEqual(ComplianceLight.Green, verdict.Light);
            verdicts.Add(verdict);
        }

        var identityFacts =
            from operatorNeeded in new[] { false, true }
            from hoursToArrival in new double[] { -5, 3, 23, 25, 200 }
            select new IdentityFacts
            {
                ArrivalStartUtc = Now.AddHours(hoursToArrival),
                NowUtc = Now,
                RedHoursBeforeArrival = 24,
                OperatorDecisionNeeded = operatorNeeded,
            };
        verdicts.AddRange(identityFacts.Select(ComplianceLightRules.Identity));

        // ISTAT without its proof, the tax with one receipt or none, a rate that is missing, a CIN that is not valid, a connection of any state.
        DateOnly[] periodStarts = [new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)];
        var istatFacts =
            from sender in Enum.GetValues<ComplianceSenderType>()
            from deadlineDay in new[] { 1, 5, 28, 31 }
            from periodStart in periodStarts
            select new IstatFacts { Sender = sender, Today = Today, PeriodStart = periodStart, DeadlineDay = deadlineDay };
        verdicts.AddRange(istatFacts.Select(ComplianceLightRules.Istat).OfType<ComplianceVerdict>());

        var taxFacts =
            from periodStart in periodStarts
            from declaration in new ComplianceProof?[] { null, Receipt }
            from payment in new ComplianceProof?[] { null, Receipt }
            where declaration is null || payment is null
            select new TaxPeriodFacts { Today = Today, PeriodStart = periodStart, Declaration = declaration, Payment = payment };
        verdicts.AddRange(taxFacts.Select(ComplianceLightRules.TouristTaxPeriod).OfType<ComplianceVerdict>());

        verdicts.Add(ComplianceLightRules.MunicipalityRate(false)!);
        verdicts.AddRange(new[] { null, "", "IT-12345-0123456789", "ITXXXXXX", "IT123456789012345" }.Select(ComplianceLightRules.Cin));
        var connectionFacts =
            from state in Enum.GetValues<ConnectionHealthState>()
            from declared in new[] { false, true }
            select new ConnectionFacts { State = state, DeclaredByHost = declared };
        verdicts.AddRange(connectionFacts.Select(ComplianceLightRules.Connection).OfType<ComplianceVerdict>());

        foreach (var ok in new bool?[] { null, true, false })
        {
            if (ComplianceLightRules.Credentials(new CredentialsFacts { LastVerificationOk = ok }) is { } credentialsVerdict)
                verdicts.Add(credentialsVerdict);
        }

        foreach (var action in Enum.GetValues<ComplianceCockpitAction>())
            verdicts.Add(ComplianceLightRules.OpenAction(action));

        Assert.True(verdicts.Count > 1000, $"Only {verdicts.Count} verdicts were checked.");
        Assert.All(verdicts, verdict =>
        {
            Assert.NotEqual(ComplianceLight.Green, verdict.Light);
            Assert.Null(verdict.ProofReference);
            Assert.NotEqual(ComplianceEvidenceClass.OfficialProof, verdict.EvidenceClass);
        });
    }

    [Fact]
    public void Green_AlwaysCarriesItsProof_AndAFailureIsNeverHiddenByOne()
    {
        foreach (var sender in Enum.GetValues<ComplianceSenderType>())
        {
            var green = ComplianceLightRules.Alloggiati(Alloggiati(sender, proof: Receipt));
            Assert.Equal(ComplianceLight.Green, green.Verdict.Light);
            Assert.Equal(ComplianceReasonCodes.ReceiptArchived, green.Verdict.Reason);
            Assert.Equal("manual-proof:1", green.Verdict.ProofReference);
            Assert.Equal(ComplianceEvidenceClass.OfficialProof, green.Verdict.EvidenceClass);
            Assert.Equal(ComplianceEvidenceSources.ManualUpload, green.Verdict.EvidenceSource);
            Assert.Equal<DateTime?>(Receipt.VerifiedAt, green.Verdict.LastVerifiedAt);
            Assert.Equal(ComplianceCockpitAction.UploadAlloggiatiReceipt, green.Action);
        }

        foreach (var status in new[] { AlloggiatiWebStatus.Errore, AlloggiatiWebStatus.Rifiutato, AlloggiatiWebStatus.PartiallyAccepted })
        {
            var red = ComplianceLightRules.Alloggiati(Alloggiati(status: status, proof: Receipt));
            Assert.Equal(ComplianceLight.Red, red.Verdict.Light);
            Assert.Equal(ComplianceCockpitAction.ResolveAlloggiatiFailure, red.Action);
        }

        // The ISTAT proof of the period, both receipts of the tax, a valid CIN and a passed identity verification are green with their reference.
        var istat = ComplianceLightRules.Istat(new IstatFacts { Sender = ComplianceSenderType.Manual, Today = Today, PeriodStart = new DateOnly(2026, 10, 1), DeadlineDay = 10, Proof = Receipt });
        Assert.Equal((ComplianceLight.Green, "manual-proof:1"), (istat!.Light, istat.ProofReference));
        var declaration = new ComplianceProof("manual-proof:2", ComplianceEvidenceSources.ManualUpload, Now.AddDays(-1));
        var tax = ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = Today, PeriodStart = new DateOnly(2026, 10, 1), Declaration = declaration, Payment = Receipt });
        Assert.Equal(ComplianceLight.Green, tax!.Light);
        Assert.Equal("manual-proof:2+manual-proof:1", tax.ProofReference);
        Assert.Equal<DateTime?>(Receipt.VerifiedAt, tax.LastVerifiedAt);
        var cin = ComplianceLightRules.Cin("IT058091C27G5FFZDZ");
        Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.CinValid, "cin:IT058091C27G5FFZDZ"), (cin.Light, cin.Reason, cin.ProofReference));
        var identity = ComplianceLightRules.Identity(new IdentityFacts { ArrivalStartUtc = Now.AddHours(2), NowUtc = Now, RedHoursBeforeArrival = 24, Proof = Receipt });
        Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.IdentityVerified), (identity.Light, identity.Reason));
    }

    [Fact]
    public void Alloggiati_SentOrDeclared_IsYellowTheDayOfTheSend_AndRedFromTheDayAfter()
    {
        foreach (var status in new[] { AlloggiatiWebStatus.InviatoManualmente, AlloggiatiWebStatus.SentReceiptPending, AlloggiatiWebStatus.Inviato })
        {
            var today = ComplianceLightRules.Alloggiati(Alloggiati(status: status, communicatedAt: Now.AddHours(-2)));
            Assert.Equal((ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceLight.Yellow, ComplianceReasonCodes.AwaitingReceipt), (today.Action, today.Verdict.Light, today.Verdict.Reason));

            var yesterday = ComplianceLightRules.Alloggiati(Alloggiati(status: status, communicatedAt: Now.AddDays(-1)));
            Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.ReceiptMissing), (yesterday.Verdict.Light, yesterday.Verdict.Reason));
        }

        var afterMidnightInRome = ComplianceLightRules.Alloggiati(Alloggiati(status: AlloggiatiWebStatus.InviatoManualmente, communicatedAt: new DateTime(2026, 11, 5, 23, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(ComplianceLight.Yellow, afterMidnightInRome.Verdict.Light);
        var beforeMidnightInRome = ComplianceLightRules.Alloggiati(Alloggiati(status: AlloggiatiWebStatus.InviatoManualmente, communicatedAt: new DateTime(2026, 11, 5, 22, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(ComplianceLight.Red, beforeMidnightInRome.Verdict.Light);
    }

    [Fact]
    public void Alloggiati_EntrustedToTheChannelManager_IsYellowUntilTheReceipt_AndRedWhenTheTermPassedWithoutOne()
    {
        var within = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.ChannelManager, entrusted: true, deadlineInHours: 5));

        Assert.Equal(ComplianceCockpitAction.UploadAlloggiatiReceipt, within.Action);
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager), (within.Verdict.Light, within.Verdict.Reason));
        Assert.Equal(ComplianceEvidenceClass.ExternalVerificationRequired, within.Verdict.EvidenceClass);
        Assert.Equal(OctorateHandoffTopic.Alloggiati, within.Verdict.HandoffTopic);
        Assert.Null(within.Verdict.ProofReference);

        var late = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.ChannelManager, entrusted: true, deadlineInHours: -1));
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.ReceiptMissing), (late.Verdict.Light, late.Verdict.Reason));
        Assert.Equal(OctorateHandoffTopic.Alloggiati, late.Verdict.HandoffTopic);

        var manual = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.Manual, entrusted: true));
        Assert.Equal((ComplianceCockpitAction.SendAlloggiati, ComplianceLight.Red), (manual.Action, manual.Verdict.Light));
    }

    [Fact]
    public void Alloggiati_WhatTheChannelManagerDoesNotSend_IsRedWithTheActionToSendByHand()
    {
        foreach (var entrusted in new[] { false, true })
        {
            var longStay = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.ChannelManager, nights: 31, entrusted: entrusted));
            Assert.Equal((ComplianceCockpitAction.SendAlloggiatiManually, ComplianceReasonCodes.OverThirtyNightsNotSent), (longStay.Action, longStay.Verdict.Reason));
        }

        var inactive = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.ChannelManager, entrusted: true, connection: ConnectionHealthState.Inactive));
        Assert.Equal((ComplianceCockpitAction.SendAlloggiatiManually, ComplianceLight.Red, ComplianceReasonCodes.ConnectionInactive), (inactive.Action, inactive.Verdict.Light, inactive.Verdict.Reason));

        var shortStay = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.ChannelManager, nights: 1));
        Assert.Equal((ComplianceCockpitAction.SendAlloggiatiManually, ComplianceReasonCodes.ShortStayAtRisk), (shortStay.Action, shortStay.Verdict.Reason));
        var entrustedShort = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.ChannelManager, nights: 1, entrusted: true));
        Assert.Equal(ComplianceLight.Yellow, entrustedShort.Verdict.Light);

        var unknown = ComplianceLightRules.Alloggiati(Alloggiati(ComplianceSenderType.ChannelManager, entrusted: true, connection: ConnectionHealthState.Unknown));
        Assert.Equal(ComplianceLight.Yellow, unknown.Verdict.Light);
    }

    [Fact]
    public void Alloggiati_DueAndNotSent_IsRed_AndLateWhenTheDeadlinePassed()
    {
        var due = ComplianceLightRules.Alloggiati(Alloggiati(deadlineInHours: 4));
        Assert.Equal((ComplianceCockpitAction.SendAlloggiati, ComplianceLight.Red, ComplianceReasonCodes.AlloggiatiNotSent), (due.Action, due.Verdict.Light, due.Verdict.Reason));

        var late = ComplianceLightRules.Alloggiati(Alloggiati(deadlineInHours: -4));
        Assert.Equal((ComplianceCockpitAction.SendAlloggiati, ComplianceReasonCodes.DeadlineOverdue), (late.Action, late.Verdict.Reason));

        var failed = ComplianceLightRules.Alloggiati(Alloggiati(status: AlloggiatiWebStatus.Rifiutato));
        Assert.Equal((ComplianceCockpitAction.ResolveAlloggiatiFailure, ComplianceReasonCodes.AlloggiatiFailed), (failed.Action, failed.Verdict.Reason));

        var partial = ComplianceLightRules.Alloggiati(Alloggiati(status: AlloggiatiWebStatus.PartiallyAccepted));
        Assert.Equal(ComplianceReasonCodes.AlloggiatiIncomplete, partial.Verdict.Reason);

        var toVerify = ComplianceLightRules.Alloggiati(Alloggiati(status: AlloggiatiWebStatus.ToVerify));
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.ExternalVerificationRequired), (toVerify.Verdict.Light, toVerify.Verdict.Reason));
        var sending = ComplianceLightRules.Alloggiati(Alloggiati(status: AlloggiatiWebStatus.Sending));
        Assert.Equal(ComplianceLight.Yellow, sending.Verdict.Light);
    }

    [Fact]
    public void Identity_YellowWhileTheSelfieIsAwaited_RedCloseToTheArrivalOrWhenAnOperatorMustDecide()
    {
        IdentityFacts Facts(double hoursToArrival, bool operatorNeeded = false) =>
            new()
            {
                ArrivalStartUtc = Now.AddHours(hoursToArrival),
                NowUtc = Now,
                RedHoursBeforeArrival = 24,
                OperatorDecisionNeeded = operatorNeeded,
            };

        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.SelfieAwaited), (ComplianceLightRules.Identity(Facts(72)).Light, ComplianceLightRules.Identity(Facts(72)).Reason));
        Assert.Equal(ComplianceLight.Yellow, ComplianceLightRules.Identity(Facts(24.5)).Light);

        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.IdentityMissing), (ComplianceLightRules.Identity(Facts(24)).Light, ComplianceLightRules.Identity(Facts(24)).Reason));
        Assert.Equal(ComplianceLight.Red, ComplianceLightRules.Identity(Facts(-2)).Light);

        var operatorVerdict = ComplianceLightRules.Identity(Facts(200, operatorNeeded: true));
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.OperatorDecisionRequired), (operatorVerdict.Light, operatorVerdict.Reason));
    }

    [Fact]
    public void Connection_RedWhenInactive_YellowWhenUnknown_AndNeverAnItemWhenActiveOrDeclared()
    {
        Assert.Equal(
            (ComplianceLight.Red, ComplianceReasonCodes.ConnectionInactive),
            (ComplianceLightRules.Connection(new ConnectionFacts { State = ConnectionHealthState.Inactive })!.Light, ComplianceLightRules.Connection(new ConnectionFacts { State = ConnectionHealthState.Inactive })!.Reason));
        Assert.Equal(ComplianceLight.Yellow, ComplianceLightRules.Connection(new ConnectionFacts { State = ConnectionHealthState.Unknown })!.Light);

        Assert.Null(ComplianceLightRules.Connection(new ConnectionFacts { State = ConnectionHealthState.Active }));
        Assert.Null(ComplianceLightRules.Connection(new ConnectionFacts { State = ConnectionHealthState.Unknown, DeclaredByHost = true }));
        Assert.Null(ComplianceLightRules.Connection(new ConnectionFacts { State = ConnectionHealthState.Manual }));
        Assert.Equal(ComplianceLight.Red, ComplianceLightRules.Connection(new ConnectionFacts { State = ConnectionHealthState.Inactive, DeclaredByHost = true })!.Light);

        Assert.Equal(ComplianceReasonCodes.CredentialsRefused, ComplianceLightRules.Credentials(new CredentialsFacts { LastVerificationOk = false })!.Reason);
        Assert.Null(ComplianceLightRules.Credentials(new CredentialsFacts { LastVerificationOk = true }));
        Assert.Null(ComplianceLightRules.Credentials(new CredentialsFacts { LastVerificationOk = null }));
    }

    [Fact]
    public void Istat_ARegionToDefineGivesNoItem_AndTheOutcomeOfAChannelManagerIsOctorates()
    {
        IstatFacts Facts(int? deadlineDay, ComplianceSenderType sender = ComplianceSenderType.Manual, DateOnly? today = null, ComplianceProof? proof = null) =>
            new() { Sender = sender, Today = today ?? Today, PeriodStart = new DateOnly(2026, 10, 1), DeadlineDay = deadlineDay, Proof = proof };

        Assert.Null(ComplianceLightRules.Istat(Facts(null)));
        Assert.Null(ComplianceLightRules.Istat(Facts(0)));
        Assert.Null(ComplianceLightRules.Istat(Facts(32)));

        var open = ComplianceLightRules.Istat(Facts(10))!;
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.PeriodOpen, ComplianceEvidenceClass.Missing), (open.Light, open.Reason, open.EvidenceClass));
        Assert.Equal(ComplianceLight.Yellow, ComplianceLightRules.Istat(Facts(10, today: new DateOnly(2026, 11, 10)))!.Light);
        Assert.Equal(
            (ComplianceLight.Red, ComplianceReasonCodes.DeadlineOverdue),
            (ComplianceLightRules.Istat(Facts(10, today: new DateOnly(2026, 11, 11)))!.Light, ComplianceLightRules.Istat(Facts(10, today: new DateOnly(2026, 11, 11)))!.Reason));

        var channelManager = ComplianceLightRules.Istat(Facts(10, ComplianceSenderType.ChannelManager))!;
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.ExternalVerificationRequired, OctorateHandoffTopic.Istat), (channelManager.Light, channelManager.Reason, channelManager.HandoffTopic));
        Assert.Equal(ComplianceEvidenceClass.ExternalVerificationRequired, channelManager.EvidenceClass);
        Assert.Equal(ComplianceLight.Red, ComplianceLightRules.Istat(Facts(10, ComplianceSenderType.ChannelManager, new DateOnly(2026, 12, 1)))!.Light);

        Assert.Equal(new DateOnly(2027, 2, 28), ComplianceLightRules.IstatDeadline(new DateOnly(2027, 1, 1), 31));
        Assert.Equal("2026-10", ComplianceLightRules.PeriodKey(new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public void TouristTax_NothingWhileOpen_RedWhenClosedWithoutBothReceipts_YellowWithOne()
    {
        var october = new DateOnly(2026, 10, 1);
        var declaration = new ComplianceProof("manual-proof:d", ComplianceEvidenceSources.ManualUpload);
        var payment = new ComplianceProof("manual-proof:p", ComplianceEvidenceSources.ManualUpload);

        Assert.Null(ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = new DateOnly(2026, 10, 31), PeriodStart = october }));
        Assert.Null(ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = Today, PeriodStart = new DateOnly(2026, 11, 1) }));

        var none = ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = Today, PeriodStart = october })!;
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.EvidenceMissing), (none.Light, none.Reason));
        var declaredOnly = ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = Today, PeriodStart = october, Declaration = declaration })!;
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.AwaitingReceipt), (declaredOnly.Light, declaredOnly.Reason));
        var paidOnly = ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = Today, PeriodStart = october, Payment = payment })!;
        Assert.Equal(ComplianceLight.Yellow, paidOnly.Light);
        Assert.Equal(
            ComplianceLight.Green,
            ComplianceLightRules.TouristTaxPeriod(new TaxPeriodFacts { Today = Today, PeriodStart = october, Declaration = declaration, Payment = payment })!.Light);

        Assert.Equal(ComplianceReasonCodes.MunicipalityRateMissing, ComplianceLightRules.MunicipalityRate(false)!.Reason);
        Assert.Null(ComplianceLightRules.MunicipalityRate(true));
    }

    [Theory]
    [InlineData(null, ComplianceReasonCodes.CinMissing)]
    [InlineData("", ComplianceReasonCodes.CinMissing)]
    [InlineData("  ", ComplianceReasonCodes.CinMissing)]
    [InlineData("IT-12345-0123456789", ComplianceReasonCodes.CinInvalid)]
    [InlineData("IT123456789012345", ComplianceReasonCodes.CinInvalid)]
    [InlineData("IT058091C27G5FFZDZ", ComplianceReasonCodes.CinValid)]
    [InlineData("it 058091 c27 g5ffzdz", ComplianceReasonCodes.CinValid)]
    public void Cin_IsComputedFromTheOfficialFormat(string? cin, string reason)
    {
        var verdict = ComplianceLightRules.Cin(cin);

        Assert.Equal(reason, verdict.Reason);
        Assert.Equal(reason == ComplianceReasonCodes.CinValid ? ComplianceLight.Green : ComplianceLight.Red, verdict.Light);
    }

    [Fact]
    public void ReasonCodes_AreStableSnakeCase_AndEveryCodeTheRulesGiveIsInTheList()
    {
        var codes = ComplianceReasonCodes.All;
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches("^[a-z]+(_[a-z]+)*$", code));

        var constants = typeof(ComplianceReasonCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);
        Assert.Equal(constants, codes.Order(StringComparer.Ordinal));

        Assert.Contains("receipt_archived", codes);
        Assert.Contains("awaiting_receipt", codes);
        Assert.Contains("entrusted_to_channel_manager", codes);
        Assert.Contains("external_verification_required", codes);
        Assert.Contains("evidence_missing", codes);
        Assert.Contains("deadline_overdue", codes);

        foreach (var action in Enum.GetValues<ComplianceCockpitAction>())
            Assert.Contains(ComplianceLightRules.OpenAction(action).Reason, codes);
        Assert.Matches(new Regex("^[a-z_]+$"), ComplianceReasonCodes.ActionRequired);
    }

    [Fact]
    public void TheEightNewActions_AreAppendedToTheCockpitEnum_WithoutMovingTheExistingOnes()
    {
        Assert.Equal(
            [
                "ActivateProperty", "CompleteGuestCheckIn", "CheckOut", "SendAlloggiati", "ResolveAlloggiatiFailure", "ConfirmPropertyReady",
                "VerifyIdentity", "UploadAlloggiatiReceipt", "ActivateOctorateSend", "SendAlloggiatiManually", "UploadIstatProof",
                "EnterMunicipalityTaxRate", "DeclareTouristTax", "RenewPoliceCredentials", "NotifyTicinoPolice", "NotifyGuestsAuthority",
            ],
            Enum.GetNames<ComplianceCockpitAction>());
        Assert.Equal(0, (int)ComplianceCockpitAction.ActivateProperty);
        Assert.Equal(5, (int)ComplianceCockpitAction.ConfirmPropertyReady);
        Assert.Equal(6, (int)ComplianceCockpitAction.VerifyIdentity);

        string[] propertyActions = ["ActivateProperty", "ActivateOctorateSend", "UploadIstatProof", "EnterMunicipalityTaxRate", "DeclareTouristTax", "RenewPoliceCredentials", "NotifyTicinoPolice", "NotifyGuestsAuthority"];
        foreach (var action in Enum.GetValues<ComplianceCockpitAction>())
            Assert.Equal(propertyActions.Contains(action.ToString()), ComplianceSummaryItem.TargetsProperty(action));
    }

    [Fact]
    public void ComplianceStatus_OctorateHandoffNeverChangesTheLight_Test()
    {
        string[] outcomeLike = ["handoff", "return", "attest", "declared", "confirm", "outcome", "verified", "passed"];
        foreach (var type in new[] { typeof(AlloggiatiFacts), typeof(IdentityFacts), typeof(IstatFacts), typeof(TaxPeriodFacts), typeof(CredentialsFacts) })
        {
            var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name.ToLowerInvariant());
            Assert.DoesNotContain(names, name => outcomeLike.Any(name.Contains));
        }

        // What the rules give for an outcome that only Octorate holds: yellow with the topic while the deadline is open, red when it passed,
        // and green only with a proof CasaZen holds. The same facts always give the same answer: the host going to the Cassetto and coming back
        // is not a fact.
        var entrusted = Alloggiati(ComplianceSenderType.ChannelManager, entrusted: true, deadlineInHours: 3);
        var before = ComplianceLightRules.Alloggiati(entrusted);
        var afterTheHostCameBack = ComplianceLightRules.Alloggiati(entrusted with { });
        Assert.Equal(before, afterTheHostCameBack);
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager, OctorateHandoffTopic.Alloggiati), (before.Verdict.Light, before.Verdict.Reason, before.Verdict.HandoffTopic));

        var overdue = ComplianceLightRules.Alloggiati(entrusted with { DeadlineUtc = Now.AddHours(-1) });
        Assert.Equal((ComplianceLight.Red, ComplianceReasonCodes.ReceiptMissing), (overdue.Verdict.Light, overdue.Verdict.Reason));

        var withProof = ComplianceLightRules.Alloggiati(entrusted with { Proof = Receipt });
        Assert.Equal(ComplianceLight.Green, withProof.Verdict.Light);

        // The ISTAT of a channel manager is the same: yellow with the topic, red when the deadline passed, green only with a proof.
        var period = new IstatFacts { Sender = ComplianceSenderType.ChannelManager, Today = Today, PeriodStart = new DateOnly(2026, 10, 1), DeadlineDay = 10 };
        Assert.Equal(ComplianceReasonCodes.ExternalVerificationRequired, ComplianceLightRules.Istat(period)!.Reason);
        Assert.Equal(ComplianceLight.Red, ComplianceLightRules.Istat(period with { Today = new DateOnly(2026, 11, 20) })!.Light);
        Assert.Equal(ComplianceLight.Green, ComplianceLightRules.Istat(period with { Proof = Receipt })!.Light);
    }
}
