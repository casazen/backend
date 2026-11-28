using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Regulatory;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Push;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Xunit.Sdk;
using World = Casazen.Tests.Integration.Acceptance.AcceptanceScenarioBuilder;

namespace Casazen.Tests.Integration.Acceptance;

/// <summary>
/// E30/F03/US01/T01 (NF-133, casazen/backend#979): the Ticino pilot from the first screen to the last night, on the real services. The flat of Quokka in Lugano is a Swiss flat
/// whose postal code (6900) and city are the only things that say where it is; the story runs the steps of the plan, one method each, in order: the place of the flat (NF-124), its
/// readiness (NF-129), the check-in of a family of three with the document of both adults and their Swiss home (NF-132), the cockpit with ONE day to notify due 24 hours from the
/// arrival, the three alerts of the 24 hours (NF-127), the file for the portal as exact bytes (NF-126), the declaration that closes the day and the confirmation a new file asks (NF-128),
/// the receipt that is kept for five years and then deleted (NF-128), and, all along, a second agency (a guest of its own arrives the same day) that sees nothing of it, whose alerts go to
/// its own address only, and no alert, push, event or line of log that holds a guest.
/// </summary>
/// <remarks>
/// <para><b>Each step names the requirement it proves.</b> The display name of a test is the default one (<c>Namespace.Class.Method</c>), because the planner of the test shards of the CI
/// (<c>scripts/ci/test_shards.py</c>) reads it to place the test in its class: the requirement is the name of the step method, and a failure says which step failed. The trait
/// <c>Scenario</c> lists the requirements (<c>--filter Scenario~NF-127</c> selects the scenario).</para>
/// <para><b>One scenario, no giant setup.</b> The world is the Ticino part of <see cref="AcceptanceScenarioBuilder"/> (<c>AcceptanceScenarioBuilder.Ticino.cs</c>): the Ticino part of the official directory of the
/// Swiss places, two agencies, and the services of the chain over one database (in memory, or PostgreSQL for the twin of the main path). Only what is outside the application is a stand-in: the
/// e-mail queue, the push queue (the bell, the pop-up and the open pages are made from it by the decorator of the application), the private bucket of the receipts and the audience of the
/// alerts. The clock is the one of the world and moves from step to step; the pilot starts on <see cref="AcceptanceScenarioBuilder.TicinoStartDay"/> and every other date is relative to it.</para>
/// </remarks>
public class TicinoPilotScenarioTests
{
    [Fact]
    [Trait("Scenario", "NF-124 NF-126 NF-127 NF-128 NF-129 NF-132 NF-133")]
    public async Task MainPath_TheFlatOfLugano_FromThePlaceToTheDeclaredDay_WithTheAlertsAndTheRetention()
    {
        await using var world = await World.CreateTicinoAsync();
        await new Pilot(world).RunAsync();
    }

    [PostgresFact]
    [Trait("Scenario", "NF-124 NF-126 NF-127 NF-128 NF-129 NF-132 NF-133")]
    public async Task MainPath_TheFlatOfLugano_FromThePlaceToTheDeclaredDay_WithTheAlertsAndTheRetention_Postgres()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("ticinopilot");
        await using var world = await World.CreateTicinoAsync(database.CreateOptions());
        await new Pilot(world).RunAsync();
    }

    private sealed class Pilot(World world)
    {
        private static readonly DateOnly Arrival = World.TicinoArrivalDay;
        private static readonly DateOnly Departure = Arrival.AddDays(3);

        private static readonly Regex[] GuestData =
        [
            Part("Muster"), Part("Erika"), Part("X1234567"), Part("S1234567"), Part("Bahnhofstrasse"), Part("ZH123456"), Part("hans.muster@"),
            Part(World.OtherGuestLastName), Part("mirko.bernasconi@"),
            Word("Hans"), Word("Lea"), Word(World.OtherGuestFirstName), Word("8001"), Word("Zürich"),
        ];

        private Guid _booking;
        private string _link = string.Empty;
        private DateTime _dueAt;
        private byte[] _expectedCsv = [];
        private Guid _receipt;

        public async Task RunAsync()
        {
            await Step(NF124_TheFlatOfLuganoIsInTicino_WithNoMunicipalityStored);
            await Step(NF129_WithoutTheStructureNumberTheFlatStaysPending_WithTheNumberItIsActive);
            await Step(NF132_TheFamilyCompletesTheCheckIn_WithTheDocumentOfBothAdultsAndTheirSwissHome);
            await Step(NF127_TheArrivalIsRegistered_AndTheCockpitShowsOneDayDue24HoursLater);
            await Step(NF133_Tenant_AnotherAgencySeesNothingOfThePendingDay);
            await Step(NF127_TheThreeAlertsAreQueuedOnceEach_AndTheFlagOffQueuesNothing);
            await Step(NF126_TheCsvOfTheDayIsTheExpectedBytes);
            await Step(NF128_DeclaringClosesTheDayAndTheCockpitItem_AndANewFileAsksForTheConfirmation);
            await Step(NF128_TheReceiptIsStoredEncrypted_AttachedToTheDeclaration_AndTheRetentionLeavesItWhileYoung);
            await Step(NF133_Tenant_AnotherAgencySeesNothingOfTheDeclaredDayNorOfItsReceipt);
            await Step(NF128_TheRetentionDeletesTheReceiptAfterFiveYears_AndKeepsTheDeclaration);
            await Step(NF128_NothingOfTheGuestsIsInTheAlertsNorInTheLogs);
        }

        private static async Task Step(Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new XunitException($"Step {step.Method.Name} failed: {ex.Message}", ex);
            }
        }

        private async Task NF124_TheFlatOfLuganoIsInTicino_WithNoMunicipalityStored()
        {
            await world.CreateTheFlatInLuganoAsync();

            await using var scope = world.OpenTicinoScope();
            var property = await scope.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.PropertyId);
            Assert.Equal(("CH", "6900", "Lugano", "CHF", "Europe/Zurich"), (property.Country, property.PostalCode, property.City, property.Currency, property.Timezone));
            Assert.Null(property.SwissMunicipalityCode);
            Assert.Null(property.TicinoStructureId);

            var place = await scope.Places.ResolveAsync(property);
            Assert.NotNull(place);
            Assert.Equal((World.LuganoMunicipality, "Lugano", "TI", SwissPlaceSource.Derived, false), (place.MunicipalityCode, place.MunicipalityName, place.CantonCode, place.Source, place.Ambiguous));
            Assert.True(place.IsInTicino);
            Assert.True(await scope.Ticino.IsPropertyInTicinoAsync(world.PropertyId));
            Assert.Null((await scope.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.PropertyId)).SwissMunicipalityCode);
        }

        private async Task NF129_WithoutTheStructureNumberTheFlatStaysPending_WithTheNumberItIsActive()
        {
            var host = new HostActivation(World.AgencyUser, RemindersAcknowledged: true);
            var part = new OnboardingPartContext(new HostScope(world.OrgId), [world.PropertyId]);

            await using (var scope = world.OpenTicinoScope())
            {
                var refused = await scope.Status.ActivateAsync(world.PropertyId, host);
                Assert.Equal(PropertyComplianceStatus.Pending, refused.Status);
                var step = Assert.Single(refused.IncompleteSteps);
                Assert.Equal(PropertyComplianceStatusService.GuestRegistrationStepId, step.Id);
                Assert.Equal([GuestRegistrationReadiness.TicinoStructureIdMissing], step.Blockers.Select(b => b.Code));
                Assert.Equal(PropertyComplianceStatus.Pending, (await scope.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.PropertyId)).ComplianceStatus);

                var open = await scope.OnboardingPart.EvaluateAsync(part, CancellationToken.None);
                Assert.False(open.IsComplete);
                Assert.Equal(OnboardingPartReasons.TicinoStructureIdMissing, open.Reason);
                var missing = Assert.Single(open.Missing);
                Assert.Equal((world.PropertyId, GuestRegistrationReadiness.TicinoStructureIdMissing), (missing.PropertyId, missing.Reason));
            }

            await world.SetTheStructureNumberAsync(" nl-12345678 ");
            await using (var scope = world.OpenTicinoScope())
            {
                var stored = await scope.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.PropertyId);
                Assert.Equal((World.TicinoStructureNumber, PropertyComplianceStatus.Pending), (stored.TicinoStructureId, stored.ComplianceStatus));

                var activated = await scope.Status.ActivateAsync(world.PropertyId, host);
                Assert.Equal(PropertyComplianceStatus.Active, activated.Status);
                Assert.Empty(activated.IncompleteSteps);

                var done = await scope.OnboardingPart.EvaluateAsync(part, CancellationToken.None);
                Assert.True(done.IsComplete);
                Assert.Equal((1, 1), (done.Done, done.Total));
                Assert.Empty(done.Missing);
            }
        }

        private async Task NF132_TheFamilyCompletesTheCheckIn_WithTheDocumentOfBothAdultsAndTheirSwissHome()
        {
            world.At(World.InRome(World.TicinoStartDay.AddDays(1), 10));
            _booking = await world.BookTheFamilyOfZurichAsync(Arrival, Departure);
            await using var scope = world.OpenTicinoScope();
            _link = await scope.CheckIn.CreateSessionAsync(_booking, world.OrgId);

            world.At(World.InRome(Arrival.AddDays(-2), 20));
            var view = await scope.CheckIn.GetPublicViewAsync(_link);
            Assert.NotNull(view);
            Assert.True(view.AskVehiclePlate);
            Assert.Equal(TicinoCheckInFields.Required, view.RequiredGuestFields);
            Assert.Equal(3, view.DeclaredGuests);

            var refused = await scope.CheckIn.SubmitAsync(
                _link,
                new GuestCheckInSubmitRequest { Guests = World.TheFamilyOfZurich(Arrival, documentOfTheSecondAdult: false), VehiclePlate = " zh 123 456 " });
            Assert.False(refused.Success);
            Assert.Equal(["1.DocumentType", "1.DocumentNumber"], refused.ValidationErrors.Select(e => $"{e.Index}.{e.Field}"));
            Assert.Empty(await scope.Db.StayGuests.AsNoTracking().ToListAsync());

            var completed = await scope.CheckIn.SubmitAsync(
                _link,
                new GuestCheckInSubmitRequest { Guests = World.TheFamilyOfZurich(Arrival), VehiclePlate = " zh 123 456 " });
            Assert.True(completed.Success, string.Join(", ", completed.ValidationErrors.Select(e => $"{e.Index}.{e.Field}")));

            await using var read = world.OpenTicinoScope();
            var rows = await read.Db.StayGuests.AsNoTracking().Where(g => g.BookingId == _booking).OrderBy(g => g.Position).ToListAsync();
            Assert.Equal([StayGuestType.HeadOfFamily, StayGuestType.FamilyMember, StayGuestType.FamilyMember], rows.Select(r => r.Type));
            Assert.Equal((GuestDocumentType.Passport, "X1234567"), (rows[0].DocumentType!.Value, rows[0].DocumentNumber));
            Assert.Equal((GuestDocumentType.IdentityCard, "S1234567"), (rows[1].DocumentType!.Value, rows[1].DocumentNumber));
            Assert.Null(rows[2].DocumentType);
            Assert.All(rows, r => Assert.Equal(("Bahnhofstrasse 12", "8001", "Zürich"), (r.ResidenceStreet, r.ResidencePostalCode, r.ResidenceCityName)));
            Assert.Equal("ZH123456", (await read.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == _booking)).VehiclePlate);
        }

        private async Task NF127_TheArrivalIsRegistered_AndTheCockpitShowsOneDayDue24HoursLater()
        {
            world.At(World.InRome(Arrival, 15, 20));
            _dueAt = world.Clock.GetUtcNow().UtcDateTime.AddHours(24);

            await using var scope = world.OpenTicinoScope();
            await scope.Stays.RegisterArrivalAsync(_booking);
            Assert.Equal(world.Clock.GetUtcNow().UtcDateTime, (await scope.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == _booking)).ArrivedAt);

            await scope.Stays.RegisterArrivalAsync(world.OtherBookingId);
            Assert.Equal(world.Clock.GetUtcNow().UtcDateTime, (await scope.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == world.OtherBookingId)).ArrivedAt);

            var summary = await scope.Cockpit.GetSummaryAsync(new HostScope(world.OrgId));
            Assert.Equal(1, summary.TicinoNotifications.Count);
            var item = Assert.Single(summary.TicinoNotifications.Items);
            Assert.Equal(ComplianceCockpitAction.NotifyTicinoPolice, item.Action);
            Assert.Equal(world.PropertyId, item.PropertyId);
            Assert.Equal(World.PropertyName, item.Label);
            Assert.Equal(Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), item.Period);
            Assert.Equal(_dueAt, item.DueAt);

            var pending = await scope.Ticino.ListPendingDaysAsync(null, null, world.Clock.TodayInRomeAsDateOnly());
            Assert.Equal(2, pending.Count);
            var own = Assert.Single(pending, p => p.OrgId == world.OrgId);
            Assert.Equal((world.PropertyId, Arrival, 1, _dueAt), (own.PropertyId, own.Day, own.StayCount, own.DueAtUtc));
            var other = Assert.Single(pending, p => p.OrgId == world.OtherOrgId);
            Assert.Equal((world.OtherPropertyId, Arrival, 1, _dueAt), (other.PropertyId, other.Day, other.StayCount, other.DueAtUtc));
        }

        private async Task NF133_Tenant_AnotherAgencySeesNothingOfThePendingDay()
        {
            await AssertEachAgencySeesOnlyItsOwnAsync(luganoDayIsPending: true);
        }

        private async Task NF127_TheThreeAlertsAreQueuedOnceEach_AndTheFlagOffQueuesNothing()
        {
            Assert.False(World.Configuration.GetValue<bool>("Features:TicinoNotificationAlerts"));
            world.At(World.InRome(Arrival, 17, 30));
            Assert.Equal(0, await world.RunTheHourlyAlertsAsync());
            Assert.Empty(AlertEmails());
            Assert.Empty(AlertPushes());
            await using (var scope = world.OpenTicinoScope())
                Assert.Empty(await scope.Db.ComplianceAlertStates.AsNoTracking().ToListAsync());

            // With the flag on, the hourly run after each stage is due sends it once: the arrival evening (two hours after the arrival time the guests said, 15:00), six hours
            // before the deadline, and the deadline. The two agencies have a day each on the same clock, so each run sends two alerts, one to each agency, and a second run of the
            // same hour (a retry, a second server) sends nothing again.
            world.Flags.Set(FeatureFlags.TicinoNotificationAlerts, true);
            (DateTimeOffset At, int Stage, string Subject)[] stages =
            [
                (World.InRome(Arrival, 17, 30), ComplianceAlertStage.TicinoFileToPrepare, "Notifica alla polizia da preparare - "),
                (World.InRome(Arrival.AddDays(1), 10), ComplianceAlertStage.TicinoDeadlineApproaching, "Notifica alla polizia in scadenza - "),
                (World.InRome(Arrival.AddDays(1), 16), ComplianceAlertStage.TicinoOverdue, "Notifica alla polizia scaduta - "),
            ];
            foreach (var (at, stage, subject) in stages)
            {
                world.At(at);
                Assert.Equal(2, await world.RunTheHourlyAlertsAsync());
                Assert.Equal(0, await world.RunTheHourlyAlertsAsync());

                await using var scope = world.OpenTicinoScope();
                var states = await scope.Db.ComplianceAlertStates.AsNoTracking().ToListAsync();
                Assert.Equal(2, states.Count);
                Assert.All(states, s => Assert.Equal(stage, s.Stage));
                Assert.StartsWith(subject + World.PropertyName, AlertEmailsTo(World.TicinoAgencyEmail)[^1].Content.Subject, StringComparison.Ordinal);
                Assert.StartsWith(subject + World.OtherPropertyName, AlertEmailsTo(World.OtherAgencyEmail)[^1].Content.Subject, StringComparison.Ordinal);
            }

            await using (var scope = world.OpenTicinoScope())
            {
                var states = await scope.Db.ComplianceAlertStates.AsNoTracking().ToListAsync();
                foreach (var (orgId, propertyId) in new[] { (world.OrgId, world.PropertyId), (world.OtherOrgId, world.OtherPropertyId) })
                {
                    var state = Assert.Single(states, s => s.PropertyId == propertyId);
                    Assert.Equal(
                        (orgId, ComplianceAlertType.TicinoNotification, Iso(Arrival), ComplianceAlertStage.TicinoOverdue, 3),
                        (state.OrgId, state.Type, state.ReferenceKey, state.Stage, state.AlertCount));
                }
            }

            var emails = AlertEmails();
            Assert.Equal(6, emails.Count);
            Assert.DoesNotContain(emails, e => e.To != World.TicinoAgencyEmail && e.To != World.OtherAgencyEmail);
            foreach (var (address, flat, theOtherFlat) in new[]
                     {
                         (World.TicinoAgencyEmail, World.PropertyName, World.OtherPropertyName),
                         (World.OtherAgencyEmail, World.OtherPropertyName, World.PropertyName),
                     })
            {
                var mine = AlertEmailsTo(address);
                Assert.Equal(3, mine.Count);
                Assert.All(mine, e => Assert.Contains(flat, e.Content.Subject, StringComparison.Ordinal));
                Assert.All(mine, e => Assert.DoesNotContain(theOtherFlat, $"{e.Content.Subject} {e.Content.HtmlBody}", StringComparison.Ordinal));
            }

            Assert.Equal(6, AlertPushes().Select(p => p.DeliveryKey).Distinct().Count());
            foreach (var propertyId in new[] { world.PropertyId, world.OtherPropertyId })
            {
                var mine = AlertPushesOf(propertyId);
                Assert.Equal([PushTypes.TicinoNotificationDue, PushTypes.TicinoNotificationDue, PushTypes.TicinoNotificationOverdue], mine.Select(p => p.Payload.Type));
                Assert.All(mine, p => Assert.Equal(PushRoutes.Properties, p.Payload.Route));
            }

            Assert.Equal(6, AlertPushesOf(world.PropertyId).Count + AlertPushesOf(world.OtherPropertyId).Count);
        }

        private async Task NF126_TheCsvOfTheDayIsTheExpectedBytes()
        {
            world.At(World.InRome(Arrival.AddDays(1), 16, 30));
            await using var scope = world.OpenTicinoScope();

            var day = await scope.Ticino.GetDayAsync(world.PropertyId, Arrival);
            Assert.Equal((World.TicinoStructureNumber, 1, 2, 0), (day.StructureId, day.StayCount, day.Plan.Registrations.Count, day.Plan.Issues.Count));
            Assert.Equal(("windows-1252", 0, 0), (day.CsvCharset, day.TransliteratedCount, day.ReceiptCount));
            Assert.Equal(_dueAt, day.DueAtUtc);
            Assert.False(day.AlreadyDeclared);
            Assert.Equal(0, day.FileGeneratedCount);

            var file = await scope.Ticino.GenerateFileAsync(world.PropertyId, Arrival, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(World.AgencyUser));

            _expectedCsv = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(ExpectedCsvText());
            Assert.Equal(_expectedCsv, file.Content);
            var model = File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "ticino", "D-Modello_2.CSV"));
            var modelHeader = model.AsSpan(0, model.AsSpan().IndexOf("\r\n"u8)).ToArray();
            Assert.Equal(modelHeader, file.Content.AsSpan(0, modelHeader.Length).ToArray());
            Assert.Contains((byte)0xE0, file.Content);
            Assert.True(file.Content.AsSpan().IndexOf(new byte[] { (byte)'Z', 0xFC, (byte)'r', (byte)'i', (byte)'c', (byte)'h' }) >= 0);
            Assert.DoesNotContain((byte)0xC3, file.Content);

            Assert.Equal(
                ($"notifiche-ti-{Iso(Arrival)}.csv", "text/csv; charset=windows-1252", $"TI-{world.PropertyId:N}-{Arrival.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}", 2, 0),
                (file.FileName, file.ContentType, file.Reference, file.Rows, file.TransliteratedCount));
            var hash = Convert.ToHexString(SHA256.HashData(_expectedCsv)).ToLowerInvariant();
            Assert.Equal(hash, file.FileHash);

            var audit = await scope.Db.SwissNotificationFileEvents.AsNoTracking().SingleAsync();
            Assert.Equal(
                (world.OrgId, world.PropertyId, Arrival, SwissNotificationFileFormats.Csv, 2, hash, World.AgencyUser),
                (audit.OrgId, audit.PropertyId, audit.ArrivalDay, audit.Format, audit.RowCount, audit.Sha256, audit.CreatedByUserId));
        }

        private async Task NF128_DeclaringClosesTheDayAndTheCockpitItem_AndANewFileAsksForTheConfirmation()
        {
            world.At(World.InRome(Arrival.AddDays(1), 16, 35));
            await using var scope = world.OpenTicinoScope();
            var today = world.Clock.TodayInRomeAsDateOnly();
            var declaredAt = world.Clock.GetUtcNow().UtcDateTime;
            Assert.Equal(1, (await scope.Cockpit.GetSummaryAsync(new HostScope(world.OrgId))).TicinoNotifications.Count);

            var declared = await scope.Ticino.DeclareDayAsync(world.PropertyId, Arrival, World.AgencyUser);
            Assert.True(declared.Created);
            Assert.Equal(World.TicinoHostName, declared.DeclaredBy);
            Assert.Equal(declaredAt, declared.DeclaredAtUtc);
            Assert.Null(declared.ReceiptId);
            Assert.Empty(await scope.Ticino.ListPendingDaysAsync(world.OrgId, null, today));
            var summary = await scope.Cockpit.GetSummaryAsync(new HostScope(world.OrgId));
            Assert.Equal(0, summary.TicinoNotifications.Count);
            Assert.Empty(summary.TicinoNotifications.Items);

            var again = await scope.Ticino.DeclareDayAsync(world.PropertyId, Arrival, World.AgencyUser);
            Assert.False(again.Created);
            Assert.Equal(declared.DeclaredAtUtc, again.DeclaredAtUtc);
            var told = Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.TicinoNotificationDeclared);
            Assert.Equal(PushAudience.PropertyHosts(world.PropertyId), told.Audience);

            var closed = await scope.Ticino.GetDayAsync(world.PropertyId, Arrival);
            Assert.True(closed.AlreadyDeclared);
            Assert.Equal(World.TicinoHostName, closed.DeclaredBy);
            Assert.Equal((DateTime?)declaredAt, closed.DeclaredAtUtc);

            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => scope.Ticino.GenerateFileAsync(world.PropertyId, Arrival, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(World.AgencyUser)));
            Assert.Equal("ticino_day_already_declared", conflict.Code);
            var confirmed = await scope.Ticino.GenerateFileAsync(
                world.PropertyId, Arrival, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(World.AgencyUser, ConfirmRegenerate: true));
            Assert.Equal(_expectedCsv, confirmed.Content);
            Assert.Equal(2, (await scope.Ticino.GetDayAsync(world.PropertyId, Arrival)).FileGeneratedCount);

            world.At(World.InRome(Arrival.AddDays(2), 17));
            Assert.Equal(1, await world.RunTheHourlyAlertsAsync());
            Assert.Equal(0, await world.RunTheHourlyAlertsAsync());
            Assert.Equal(3, AlertEmailsTo(World.TicinoAgencyEmail).Count);
            Assert.Equal(3, AlertPushesOf(world.PropertyId).Count);
            Assert.Equal(4, AlertEmailsTo(World.OtherAgencyEmail).Count);
            Assert.Equal(4, AlertPushesOf(world.OtherPropertyId).Count);
            Assert.Contains(World.OtherPropertyName, AlertEmailsTo(World.OtherAgencyEmail)[^1].Content.Subject, StringComparison.Ordinal);
        }

        private async Task NF128_TheReceiptIsStoredEncrypted_AttachedToTheDeclaration_AndTheRetentionLeavesItWhileYoung()
        {
            world.At(World.InRome(Arrival.AddDays(2), 17, 30));
            var original = "%PDF-1.4 TEST ricevuta del portale notifiche.ti.ch"u8.ToArray();
            await using var scope = world.OpenTicinoScope();
            using var content = new MemoryStream(original);
            var receipt = await scope.Receipts.RecordAsync(
                world.PropertyId, SwissComplianceFileType.Police, World.StoredDay(Arrival), "TEST-TI-0001", content, "ricevuta.pdf", "application/pdf");
            _receipt = receipt.Id;
            Assert.Equal(("Police", World.StoredDay(Arrival), world.OrgId), (receipt.Type, receipt.ReportDate, receipt.OrgId));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), receipt.UploadedFileHash);

            var key = StorageKeys.SwissComplianceReceipt(world.OrgId, world.PropertyId, _receipt);
            await using (var stored = await world.Storage.OpenReadAsync(StorageBucket.Private, key))
            {
                Assert.NotNull(stored);
                using var bytes = new MemoryStream();
                await stored.CopyToAsync(bytes);
                Assert.True(bytes.Length > 0);
                Assert.True(bytes.ToArray().AsSpan().IndexOf(original) < 0);
            }

            var file = await scope.Receipts.OpenFileAsync(world.PropertyId, _receipt);
            await using (var opened = file.Content)
            {
                using var bytes = new MemoryStream();
                await opened.CopyToAsync(bytes);
                Assert.Equal(original, bytes.ToArray());
            }

            Assert.Equal(("ricevuta.pdf", "application/pdf"), (file.FileName, file.ContentType));
            var listed = await scope.Receipts.ListAsync(world.PropertyId);
            Assert.Equal(1, listed.TotalCount);
            Assert.True(Assert.Single(listed.Items).HasFile);

            var attached = await scope.Ticino.DeclareDayAsync(world.PropertyId, Arrival, World.AgencyUser, _receipt);
            Assert.False(attached.Created);
            Assert.Equal(_receipt, attached.ReceiptId);
            Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.TicinoNotificationDeclared);

            await world.RunTheRetentionAsync();
            Assert.Equal((true, true), await ReceiptStateAsync());
        }

        private async Task NF133_Tenant_AnotherAgencySeesNothingOfTheDeclaredDayNorOfItsReceipt()
        {
            await AssertEachAgencySeesOnlyItsOwnAsync(luganoDayIsPending: false);
        }

        private async Task NF128_TheRetentionDeletesTheReceiptAfterFiveYears_AndKeepsTheDeclaration()
        {
            world.At(World.InRome(Arrival.AddYears(5), 5));
            await world.RunTheRetentionAsync();
            Assert.Equal((true, true), await ReceiptStateAsync());

            world.At(World.InRome(Arrival.AddYears(5).AddDays(1), 5));
            await world.RunTheRetentionAsync();
            Assert.Equal((false, false), await ReceiptStateAsync());

            await using var scope = world.OpenTicinoScope();
            var declaration = await scope.Db.SwissNotificationDeclarations.AsNoTracking().SingleAsync();
            Assert.Equal((world.PropertyId, Arrival), (declaration.PropertyId, declaration.ArrivalDay));
            if (scope.Db.Database.IsNpgsql())
            {
                Assert.Null(declaration.ReceiptId);

                // What only the database keeps: a day is declared once even when two people declare it at the same moment (the service replays the first declaration, but it is the unique index
                // that decides the race). A second declaration of the day inserted straight into the table is refused with 23505 on that index.
                await using var duplicate = world.OpenTicinoScope();
                duplicate.Db.SwissNotificationDeclarations.Add(new SwissNotificationDeclaration
                {
                    OrgId = world.OrgId,
                    PropertyId = world.PropertyId,
                    ArrivalDay = Arrival,
                    DeclaredByUserId = World.AgencyUser,
                    DeclaredAt = world.Clock.GetUtcNow().UtcDateTime,
                });
                var refused = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.Db.SaveChangesAsync());
                var violation = Assert.IsType<PostgresException>(refused.InnerException);
                Assert.Equal((PostgresErrorCodes.UniqueViolation, SwissNotificationDeclaration.DefaultRegimeIndexName), (violation.SqlState, violation.ConstraintName));
            }
        }

        private async Task NF128_NothingOfTheGuestsIsInTheAlertsNorInTheLogs()
        {
            List<string> events;
            await using (var scope = world.OpenTicinoScope())
                events = await scope.Db.OutboxEvents.AsNoTracking().Select(e => e.PayloadJson).ToListAsync();

            var told = world.Emails.Snapshot().Select(e => $"{e.Content.Subject} {e.Content.HtmlBody}")
                .Concat(world.Pushes.Queued.Select(p => $"{p.Payload.Title} {p.Payload.Body}"))
                .Concat(events)
                .ToList();
            Assert.NotEmpty(events);
            Assert.NotEmpty(told);
            Assert.True(world.Log.Count > 0);
            Assert.Contains("declared by", world.Log.AllOutput, StringComparison.Ordinal);

            Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.GuestCheckInCompleted);

            Assert.All(GuestData, personal => Assert.Matches(personal, TypedByTheGuests));
            Assert.DoesNotMatch(Word("Lea"), "cleaning and release");

            foreach (var personal in GuestData)
            {
                Assert.DoesNotContain(told, text => personal.IsMatch(text));
                Assert.False(personal.IsMatch(world.Log.AllOutput), $"A line of log holds the data of a guest: {personal}");
            }
        }

        /// <summary>
        /// The two agencies, each with a day of its own on the same date, and the tenant filter on, scoped to the org of the caller, as in every authenticated request. Each one reads its
        /// own data (the positive control: the filter does not hide what is its own, and the list the job reads for every org gives it its own day only) and nothing of the other one: the
        /// flat, the day, the file, the declaration, its undo, the sheet of a group and the receipts of the other agency do not exist for it, whatever it asks. The day of Locarno is
        /// pending in both states of the day of Lugano, so what Lugano is asked about it is never empty for want of data.
        /// </summary>
        private async Task AssertEachAgencySeesOnlyItsOwnAsync(bool luganoDayIsPending)
        {
            await AssertAgencyReadsItsOwnAsync(world.OrgId, world.PropertyId, World.TicinoStructureNumber, luganoDayIsPending);
            await AssertAgencyReadsItsOwnAsync(world.OtherOrgId, world.OtherPropertyId, World.OtherStructureNumber, dayIsPending: true);

            await AssertNothingOfAsync(World.OtherAgencyUser, world.OtherOrgId, world.OrgId, world.PropertyId, _booking, _receipt);
            await AssertNothingOfAsync(World.AgencyUser, world.OrgId, world.OtherOrgId, world.OtherPropertyId, world.OtherBookingId, Guid.Empty);
        }

        private async Task AssertAgencyReadsItsOwnAsync(Guid orgId, Guid propertyId, string structureNumber, bool dayIsPending)
        {
            await using var scope = world.OpenTicinoScopeOf(orgId);
            var today = world.Clock.TodayInRomeAsDateOnly();

            Assert.Equal(dayIsPending ? 1 : 0, (await scope.Cockpit.GetSummaryAsync(new HostScope(orgId))).TicinoNotifications.Count);

            var days = await scope.Ticino.ListPendingDaysAsync(null, null, today);
            if (dayIsPending)
            {
                var day = Assert.Single(days);
                Assert.Equal((orgId, propertyId, Arrival, 1), (day.OrgId, day.PropertyId, day.Day, day.StayCount));
            }
            else
            {
                Assert.Empty(days);
            }

            Assert.True(await scope.Ticino.IsPropertyInTicinoAsync(propertyId));
            var detail = await scope.Ticino.GetDayAsync(propertyId, Arrival);
            Assert.Equal((structureNumber, 1, !dayIsPending), (detail.StructureId, detail.StayCount, detail.AlreadyDeclared));
        }

        private async Task AssertNothingOfAsync(string callerUser, Guid callerOrg, Guid victimOrg, Guid victimProperty, Guid victimBooking, Guid victimReceipt)
        {
            await using var scope = world.OpenTicinoScopeOf(callerOrg);
            var today = world.Clock.TodayInRomeAsDateOnly();

            Assert.Empty(await scope.Ticino.ListPendingDaysAsync(victimOrg, null, today));
            Assert.False(await scope.Ticino.IsPropertyInTicinoAsync(victimProperty));

            Func<Task>[] reads =
            [
                () => scope.Ticino.GetDayAsync(victimProperty, Arrival),
                () => scope.Ticino.GenerateFileAsync(victimProperty, Arrival, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(callerUser, ConfirmRegenerate: true)),
                () => scope.Ticino.DeclareDayAsync(victimProperty, Arrival, callerUser),
                () => scope.Ticino.UndeclareDayAsync(victimProperty, Arrival, callerUser),
                () => scope.Ticino.GetGroupSheetAsync(victimProperty, victimBooking),
                () => scope.Receipts.ListAsync(victimProperty),
                () => scope.Receipts.OpenFileAsync(victimProperty, victimReceipt),
            ];
            foreach (var read in reads)
            {
                var notFound = await Assert.ThrowsAsync<NotFoundException>(read);
                Assert.Equal("property_not_found", notFound.Code);
            }
        }

        private async Task<(bool Row, bool File)> ReceiptStateAsync()
        {
            await using var scope = world.OpenTicinoScope();
            return (
                await scope.Db.SwissComplianceReceipts.AsNoTracking().AnyAsync(r => r.Id == _receipt),
                await world.Storage.ExistsAsync(StorageBucket.Private, StorageKeys.SwissComplianceReceipt(world.OrgId, world.PropertyId, _receipt)));
        }

        private List<(string? To, EmailContent Content, string Template)> AlertEmails() =>
            world.Emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.TicinoNotificationAlert).ToList();

        private List<(string? To, EmailContent Content, string Template)> AlertEmailsTo(string address) =>
            AlertEmails().Where(e => e.To == address).ToList();

        private List<QueuedPushRecord> AlertPushes() =>
            world.Pushes.Queued.Where(p => p.Payload.Type is PushTypes.TicinoNotificationDue or PushTypes.TicinoNotificationOverdue).ToList();

        private List<QueuedPushRecord> AlertPushesOf(Guid propertyId) =>
            AlertPushes().Where(p => p.Audience.Equals(PushAudience.PropertyHosts(propertyId, ComplianceAlertAudience.AlloggiatiSubmit))).ToList();

        private const string TypedByTheGuests =
            "Hans Muster (hans.muster@ospiti-test.ch), passport X1234567; Erika Muster, identity card S1234567; Lea Muster; Bahnhofstrasse 12, 8001 Zürich; plate ZH123456; "
            + "Mirko Bernasconi (mirko.bernasconi@ospiti-test.ch)";

        private static Regex Part(string text) => new(Regex.Escape(text), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static Regex Word(string text) => new($@"\b{Regex.Escape(text)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string ExpectedCsvText() =>
            string.Join(
                "\r\n",
                "Id Struttura;Cognome;Nome;Data di nascita;Nazionalità;Data di arrivo;Data di partenza;Luogo di nascita;Numero bollettino;Indirizzo;CAP;Domicilio;Nazione;Numero autovettura;Mag14;Min14;Tipo documento;Numero documento",
                $"{World.TicinoStructureNumber};Muster;Hans;{SwissDate(World.BirthOfTheHead(Arrival))};CHE;{SwissDate(Arrival)};{SwissDate(Departure)};Svizzera;QK519-2AB7M;Bahnhofstrasse 12;8001;Zürich;CHE;ZH123456;0;1;Passaporto;X1234567",
                $"{World.TicinoStructureNumber};Muster;Erika;{SwissDate(World.BirthOfTheSecondAdult(Arrival))};CHE;{SwissDate(Arrival)};{SwissDate(Departure)};Germania;QK519-2AB7M;Bahnhofstrasse 12;8001;Zürich;CHE;;0;0;Carta identità;S1234567");

        private static string SwissDate(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
