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
    // Percorso principale, in memoria: dall'appartamento di Lugano senza comune alla giornata dichiarata, con gli avvisi e la conservazione di cinque anni.
    [Fact]
    [Trait("Scenario", "NF-124 NF-126 NF-127 NF-128 NF-129 NF-132 NF-133")]
    public async Task MainPath_TheFlatOfLugano_FromThePlaceToTheDeclaredDay_WithTheAlertsAndTheRetention()
    {
        await using var world = await World.CreateTicinoAsync();
        await new Pilot(world).RunAsync();
    }

    // Percorso principale su PostgreSQL: lo stesso scenario sul database vero (chiavi esterne, indici univoci, SET NULL della ricevuta).
    [PostgresFact]
    [Trait("Scenario", "NF-124 NF-126 NF-127 NF-128 NF-129 NF-132 NF-133")]
    public async Task MainPath_TheFlatOfLugano_FromThePlaceToTheDeclaredDay_WithTheAlertsAndTheRetention_Postgres()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("ticinopilot");
        await using var world = await World.CreateTicinoAsync(database.CreateOptions());
        await new Pilot(world).RunAsync();
    }

    /// <summary>The story: the steps in order, over one world, with what each step leaves for the next one.</summary>
    private sealed class Pilot(World world)
    {
        private static readonly DateOnly Arrival = World.TicinoArrivalDay;
        private static readonly DateOnly Departure = Arrival.AddDays(3);

        /// <summary>
        /// What the guests typed, and must never be in an alert, a push, an event of the outbox or a line of log: the surnames, the documents, the street, the plate and the addresses as they are
        /// (the long ones), and the first names, the postal code and the city of the home as whole words (so «Lea» is not found in «cleaning»). Both families are searched: Lugano's and Locarno's.
        /// </summary>
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

        /// <summary>Runs one step; a failure says which step, that is which requirement, it was.</summary>
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

        // ------------------------------------------------------------ NF-124: where the flat is

        private async Task NF124_TheFlatOfLuganoIsInTicino_WithNoMunicipalityStored()
        {
            await world.CreateTheFlatInLuganoAsync();

            await using var scope = world.OpenTicinoScope();
            var property = await scope.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.PropertyId);
            Assert.Equal(("CH", "6900", "Lugano", "CHF", "Europe/Zurich"), (property.Country, property.PostalCode, property.City, property.Currency, property.Timezone));
            Assert.Null(property.SwissMunicipalityCode);
            Assert.Null(property.TicinoStructureId);

            // The one resolver places it from the postal code and the city, with the official number of Lugano; it is derived, never stored behind the host's back.
            var place = await scope.Places.ResolveAsync(property);
            Assert.NotNull(place);
            Assert.Equal((World.LuganoMunicipality, "Lugano", "TI", SwissPlaceSource.Derived, false), (place.MunicipalityCode, place.MunicipalityName, place.CantonCode, place.Source, place.Ambiguous));
            Assert.True(place.IsInTicino);
            Assert.True(await scope.Ticino.IsPropertyInTicinoAsync(world.PropertyId));
            Assert.Null((await scope.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.PropertyId)).SwissMunicipalityCode);
        }

        // ------------------------------------------------------------ NF-129: what the flat lacks to be notified

        private async Task NF129_WithoutTheStructureNumberTheFlatStaysPending_WithTheNumberItIsActive()
        {
            var host = new HostActivation(World.AgencyUser, RemindersAcknowledged: true);
            var part = new OnboardingPartContext(new HostScope(world.OrgId), [world.PropertyId]);

            await using (var scope = world.OpenTicinoScope())
            {
                // The activation is refused with the reason, and the flat stays pending.
                var refused = await scope.Status.ActivateAsync(world.PropertyId, host);
                Assert.Equal(PropertyComplianceStatus.Pending, refused.Status);
                var step = Assert.Single(refused.IncompleteSteps);
                Assert.Equal(PropertyComplianceStatusService.GuestRegistrationStepId, step.Id);
                Assert.Equal([GuestRegistrationReadiness.TicinoStructureIdMissing], step.Blockers.Select(b => b.Code));
                Assert.Equal(PropertyComplianceStatus.Pending, (await scope.Db.Properties.AsNoTracking().SingleAsync(p => p.Id == world.PropertyId)).ComplianceStatus);

                // The onboarding part of the guest registration says the same, property by property.
                var open = await scope.OnboardingPart.EvaluateAsync(part, CancellationToken.None);
                Assert.False(open.IsComplete);
                Assert.Equal(OnboardingPartReasons.TicinoStructureIdMissing, open.Reason);
                var missing = Assert.Single(open.Missing);
                Assert.Equal((world.PropertyId, GuestRegistrationReadiness.TicinoStructureIdMissing), (missing.PropertyId, missing.Reason));
            }

            // The host types the number as the form of the property takes it; the update stores it normalised and the re-evaluation never publishes a pending flat by itself.
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

        // ------------------------------------------------------------ NF-132: the check-in of the family

        private async Task NF132_TheFamilyCompletesTheCheckIn_WithTheDocumentOfBothAdultsAndTheirSwissHome()
        {
            // The family books the flat the day after the start: two adults and a girl of nine; the link of the check-in is issued with the booking.
            world.At(World.InRome(World.TicinoStartDay.AddDays(1), 10));
            _booking = await world.BookTheFamilyOfZurichAsync(Arrival, Departure);
            await using var scope = world.OpenTicinoScope();
            _link = await scope.CheckIn.CreateSessionAsync(_booking, world.OrgId);

            // Two evenings before the arrival the guests open the link: for a flat of Ticino the page asks the document of every adult, the home and the plate of the car.
            world.At(World.InRome(Arrival.AddDays(-2), 20));
            var view = await scope.CheckIn.GetPublicViewAsync(_link);
            Assert.NotNull(view);
            Assert.True(view.AskVehiclePlate);
            Assert.Equal(TicinoCheckInFields.Required, view.RequiredGuestFields);
            Assert.Equal(3, view.DeclaredGuests);

            // The wife leaves her document empty: the police file notifies every adult, so the check-in is refused with the two fields and nothing is saved.
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

        // ------------------------------------------------------------ NF-127: the cockpit, 24 hours from the arrival

        private async Task NF127_TheArrivalIsRegistered_AndTheCockpitShowsOneDayDue24HoursLater()
        {
            // 15:20 in Lugano on the arrival day: the host registers the arrival of the family. The deadline of the police is 24 hours from that moment.
            world.At(World.InRome(Arrival, 15, 20));
            _dueAt = world.Clock.GetUtcNow().UtcDateTime.AddHours(24);

            await using var scope = world.OpenTicinoScope();
            await scope.Stays.RegisterArrivalAsync(_booking);
            Assert.Equal(world.Clock.GetUtcNow().UtcDateTime, (await scope.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == _booking)).ArrivedAt);

            // The guest of Locarno arrives in the same minute: the two days run on the same clock, so each hourly run has two alerts due at once and has to tell the two agencies apart.
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

            // The list the alerts read is the same list, for every org: one day with one stay for each agency, each on its own flat and with its own deadline.
            var pending = await scope.Ticino.ListPendingDaysAsync(null, null, world.Clock.TodayInRomeAsDateOnly());
            Assert.Equal(2, pending.Count);
            var own = Assert.Single(pending, p => p.OrgId == world.OrgId);
            Assert.Equal((world.PropertyId, Arrival, 1, _dueAt), (own.PropertyId, own.Day, own.StayCount, own.DueAtUtc));
            var other = Assert.Single(pending, p => p.OrgId == world.OtherOrgId);
            Assert.Equal((world.OtherPropertyId, Arrival, 1, _dueAt), (other.PropertyId, other.Day, other.StayCount, other.DueAtUtc));
        }

        // ------------------------------------------------------------ tenant (NF-133): the two agencies, while the day of Lugano is pending

        private async Task NF133_Tenant_AnotherAgencySeesNothingOfThePendingDay()
        {
            await AssertEachAgencySeesOnlyItsOwnAsync(luganoDayIsPending: true);
        }

        // ------------------------------------------------------------ NF-127: the three alerts

        private async Task NF127_TheThreeAlertsAreQueuedOnceEach_AndTheFlagOffQueuesNothing()
        {
            // The shipped configuration has the alerts off; with the flag off the hourly job reads no day, writes no state and queues nothing.
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

            // Each agency got its three e-mails at its own address, naming its own flat and never the other one, and three pushes to the hosts of its own flat, each with a key of its own:
            // the bell, the pop-up and the open pages are made from them. The audience of an alert is the org of its flat, even when the job reads the days of every org at once.
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

        // ------------------------------------------------------------ NF-126: the file for the portal

        private async Task NF126_TheCsvOfTheDayIsTheExpectedBytes()
        {
            // 16:30 on the day after the arrival, the deadline has passed: the host opens the day and downloads the file.
            world.At(World.InRome(Arrival.AddDays(1), 16, 30));
            await using var scope = world.OpenTicinoScope();

            var day = await scope.Ticino.GetDayAsync(world.PropertyId, Arrival);
            Assert.Equal((World.TicinoStructureNumber, 1, 2, 0), (day.StructureId, day.StayCount, day.Plan.Registrations.Count, day.Plan.Issues.Count));
            Assert.Equal(("windows-1252", 0, 0), (day.CsvCharset, day.TransliteratedCount, day.ReceiptCount));
            Assert.Equal(_dueAt, day.DueAtUtc);
            Assert.False(day.AlreadyDeclared);
            Assert.Equal(0, day.FileGeneratedCount);

            var file = await scope.Ticino.GenerateFileAsync(world.PropertyId, Arrival, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(World.AgencyUser));

            // The bytes: the header of the official model, the two adults (the girl is counted in «Min14» on the head of the family, who also has the plate), in Windows-1252.
            _expectedCsv = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(ExpectedCsvText());
            Assert.Equal(_expectedCsv, file.Content);
            var model = File.ReadAllBytes(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "ticino", "D-Modello_2.CSV"));
            var modelHeader = model.AsSpan(0, model.AsSpan().IndexOf("\r\n"u8)).ToArray();
            Assert.Equal(modelHeader, file.Content.AsSpan(0, modelHeader.Length).ToArray());
            Assert.Contains((byte)0xE0, file.Content); // «à» of «Nazionalità» and of «identità» is the single byte of the model
            Assert.True(file.Content.AsSpan().IndexOf(new byte[] { (byte)'Z', 0xFC, (byte)'r', (byte)'i', (byte)'c', (byte)'h' }) >= 0); // «Zürich» with its single byte
            Assert.DoesNotContain((byte)0xC3, file.Content); // and no UTF-8 sequence

            Assert.Equal(
                ($"notifiche-ti-{Iso(Arrival)}.csv", "text/csv; charset=windows-1252", $"TI-{world.PropertyId:N}-{Arrival.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}", 2, 0),
                (file.FileName, file.ContentType, file.Reference, file.Rows, file.TransliteratedCount));
            var hash = Convert.ToHexString(SHA256.HashData(_expectedCsv)).ToLowerInvariant();
            Assert.Equal(hash, file.FileHash);

            // Every generation leaves one audit row: the format, the rows and the hash, and the person who asked; no guest, no value.
            var audit = await scope.Db.SwissNotificationFileEvents.AsNoTracking().SingleAsync();
            Assert.Equal(
                (world.OrgId, world.PropertyId, Arrival, SwissNotificationFileFormats.Csv, 2, hash, World.AgencyUser),
                (audit.OrgId, audit.PropertyId, audit.ArrivalDay, audit.Format, audit.RowCount, audit.Sha256, audit.CreatedByUserId));
        }

        // ------------------------------------------------------------ NF-128: the declaration closes the day

        private async Task NF128_DeclaringClosesTheDayAndTheCockpitItem_AndANewFileAsksForTheConfirmation()
        {
            world.At(World.InRome(Arrival.AddDays(1), 16, 35));
            await using var scope = world.OpenTicinoScope();
            var today = world.Clock.TodayInRomeAsDateOnly();
            var declaredAt = world.Clock.GetUtcNow().UtcDateTime;
            Assert.Equal(1, (await scope.Cockpit.GetSummaryAsync(new HostScope(world.OrgId))).TicinoNotifications.Count);

            // The host uploaded the file on the portal and says so, with no receipt (it is optional): the day is closed, the cockpit has nothing to notify, and the declaration
            // says who declared it and when (NF-128 AC1).
            var declared = await scope.Ticino.DeclareDayAsync(world.PropertyId, Arrival, World.AgencyUser);
            Assert.True(declared.Created);
            Assert.Equal(World.TicinoHostName, declared.DeclaredBy);
            Assert.Equal(declaredAt, declared.DeclaredAtUtc);
            Assert.Null(declared.ReceiptId);
            Assert.Empty(await scope.Ticino.ListPendingDaysAsync(world.OrgId, null, today));
            var summary = await scope.Cockpit.GetSummaryAsync(new HostScope(world.OrgId));
            Assert.Equal(0, summary.TicinoNotifications.Count);
            Assert.Empty(summary.TicinoNotifications.Items);

            // Declaring again changes nothing and tells nobody: the first declaration answers, and the other hosts of the flat were told once.
            var again = await scope.Ticino.DeclareDayAsync(world.PropertyId, Arrival, World.AgencyUser);
            Assert.False(again.Created);
            Assert.Equal(declared.DeclaredAtUtc, again.DeclaredAtUtc);
            var told = Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.TicinoNotificationDeclared);
            Assert.Equal(PushAudience.PropertyHosts(world.PropertyId), told.Audience);

            var closed = await scope.Ticino.GetDayAsync(world.PropertyId, Arrival);
            Assert.True(closed.AlreadyDeclared);
            Assert.Equal(World.TicinoHostName, closed.DeclaredBy);
            Assert.Equal((DateTime?)declaredAt, closed.DeclaredAtUtc);

            // The portal may create duplicates: a new file of a closed day asks for the confirmation (409) and, once confirmed, is the same file.
            var conflict = await Assert.ThrowsAsync<DomainConflictException>(
                () => scope.Ticino.GenerateFileAsync(world.PropertyId, Arrival, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(World.AgencyUser)));
            Assert.Equal("ticino_day_already_declared", conflict.Code);
            var confirmed = await scope.Ticino.GenerateFileAsync(
                world.PropertyId, Arrival, TicinoPoliceFileFormat.Csv, new TicinoFileRequest(World.AgencyUser, ConfirmRegenerate: true));
            Assert.Equal(_expectedCsv, confirmed.Content);
            Assert.Equal(2, (await scope.Ticino.GetDayAsync(world.PropertyId, Arrival)).FileGeneratedCount);

            // A closed day stops the alerts: the first reminder of an overdue day is due now, a day after the deadline. It goes to the agency of Locarno, whose day is still open (the day of
            // Lugano, declared, is not the one of Locarno); the agency of Lugano gets none, neither by e-mail nor by push.
            world.At(World.InRome(Arrival.AddDays(2), 17));
            Assert.Equal(1, await world.RunTheHourlyAlertsAsync());
            Assert.Equal(0, await world.RunTheHourlyAlertsAsync());
            Assert.Equal(3, AlertEmailsTo(World.TicinoAgencyEmail).Count);
            Assert.Equal(3, AlertPushesOf(world.PropertyId).Count);
            Assert.Equal(4, AlertEmailsTo(World.OtherAgencyEmail).Count);
            Assert.Equal(4, AlertPushesOf(world.OtherPropertyId).Count);
            Assert.Contains(World.OtherPropertyName, AlertEmailsTo(World.OtherAgencyEmail)[^1].Content.Subject, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------ NF-128: the receipt

        private async Task NF128_TheReceiptIsStoredEncrypted_AttachedToTheDeclaration_AndTheRetentionLeavesItWhileYoung()
        {
            // Two days after the arrival the host uploads the receipt of the portal (a PDF) in the archive: dated on the arrival day, type «Police».
            world.At(World.InRome(Arrival.AddDays(2), 17, 30));
            var original = "%PDF-1.4 TEST ricevuta del portale notifiche.ti.ch"u8.ToArray();
            await using var scope = world.OpenTicinoScope();
            using var content = new MemoryStream(original);
            var receipt = await scope.Receipts.RecordAsync(
                world.PropertyId, SwissComplianceFileType.Police, World.StoredDay(Arrival), "TEST-TI-0001", content, "ricevuta.pdf", "application/pdf");
            _receipt = receipt.Id;
            Assert.Equal(("Police", World.StoredDay(Arrival), world.OrgId), (receipt.Type, receipt.ReportDate, receipt.OrgId));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), receipt.UploadedFileHash);

            // What is in the private bucket is the encrypted payload, not the file; the archive gives the original back.
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

            // The receipt joins the declaration that has none; the day stays closed and nobody is told again.
            var attached = await scope.Ticino.DeclareDayAsync(world.PropertyId, Arrival, World.AgencyUser, _receipt);
            Assert.False(attached.Created);
            Assert.Equal(_receipt, attached.ReceiptId);
            Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.TicinoNotificationDeclared);

            // The retention job runs every night: the receipt is two days old, it is left alone.
            await world.RunTheRetentionAsync();
            Assert.Equal((true, true), await ReceiptStateAsync());
        }

        // ------------------------------------------------------------ tenant (NF-133): the two agencies, with the declaration and the receipt of Lugano

        private async Task NF133_Tenant_AnotherAgencySeesNothingOfTheDeclaredDayNorOfItsReceipt()
        {
            await AssertEachAgencySeesOnlyItsOwnAsync(luganoDayIsPending: false);
        }

        // ------------------------------------------------------------ NF-128: five years

        private async Task NF128_TheRetentionDeletesTheReceiptAfterFiveYears_AndKeepsTheDeclaration()
        {
            // The night the receipt is exactly five years old its day is the cut day, and a document dated on it is kept.
            world.At(World.InRome(Arrival.AddYears(5), 5));
            await world.RunTheRetentionAsync();
            Assert.Equal((true, true), await ReceiptStateAsync());

            // The night after, it is past its time: the encrypted file goes first, then the row.
            world.At(World.InRome(Arrival.AddYears(5).AddDays(1), 5));
            await world.RunTheRetentionAsync();
            Assert.Equal((false, false), await ReceiptStateAsync());

            // The declaration outlives the receipt (on PostgreSQL the foreign key puts NULL where the receipt was).
            await using var scope = world.OpenTicinoScope();
            var declaration = await scope.Db.SwissNotificationDeclarations.AsNoTracking().SingleAsync();
            Assert.Equal((world.PropertyId, Arrival), (declaration.PropertyId, declaration.ArrivalDay));
            if (scope.Db.Database.IsNpgsql())
                Assert.Null(declaration.ReceiptId);
        }

        // ------------------------------------------------------------ NF-128: no personal data

        private async Task NF128_NothingOfTheGuestsIsInTheAlertsNorInTheLogs()
        {
            // What the hosts are told (the alerts, the declarations and the check-in that was completed), what the services leave to the other handlers (the events of the outbox) and what they
            // log name the property and the day, ids and counts, never a guest.
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

            // The push «the guests completed the check-in» is one of the pilot too: the service of the check-in is built with the queue, so it is among the texts searched.
            Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.GuestCheckInCompleted);

            // The control: every term finds what the guests typed (so a leak would be found), and «Lea» is a word, not the middle of «cleaning».
            Assert.All(GuestData, personal => Assert.Matches(personal, TypedByTheGuests));
            Assert.DoesNotMatch(Word("Lea"), "cleaning and release");

            foreach (var personal in GuestData)
            {
                Assert.DoesNotContain(told, text => personal.IsMatch(text));
                Assert.False(personal.IsMatch(world.Log.AllOutput), $"A line of log holds the data of a guest: {personal}");
            }
        }

        // ------------------------------------------------------------ what the steps share

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

        /// <summary>A person of the agency <paramref name="orgId"/> (tenant filter on) reads the cockpit, the list of the days and the day of its own flat.</summary>
        private async Task AssertAgencyReadsItsOwnAsync(Guid orgId, Guid propertyId, string structureNumber, bool dayIsPending)
        {
            await using var scope = world.OpenTicinoScopeOf(orgId);
            var today = world.Clock.TodayInRomeAsDateOnly();

            Assert.Equal(dayIsPending ? 1 : 0, (await scope.Cockpit.GetSummaryAsync(new HostScope(orgId))).TicinoNotifications.Count);

            // The list the job reads for every org, read with the filter on: its own day, and nothing of the other agency.
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

        /// <summary>
        /// A person of the agency <paramref name="callerOrg"/> (tenant filter on) asks for the data of the flat <paramref name="victimProperty"/> of the agency <paramref name="victimOrg"/>:
        /// the list of its days is empty, its flat is not a flat it can see, and every other read says <c>property_not_found</c>.
        /// </summary>
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

        /// <summary>The alerts of the 24 hours the e-mail queue got, in the order they were queued.</summary>
        private List<(string? To, EmailContent Content, string Template)> AlertEmails() =>
            world.Emails.Snapshot().Where(e => e.Template == EmailTemplates.Names.TicinoNotificationAlert).ToList();

        /// <summary>The alerts of the 24 hours the e-mail queue got for <paramref name="address"/>, in the order they were queued.</summary>
        private List<(string? To, EmailContent Content, string Template)> AlertEmailsTo(string address) =>
            AlertEmails().Where(e => e.To == address).ToList();

        /// <summary>The pushes of the alerts of the 24 hours (the declarations are other pushes).</summary>
        private List<QueuedPushRecord> AlertPushes() =>
            world.Pushes.Queued.Where(p => p.Payload.Type is PushTypes.TicinoNotificationDue or PushTypes.TicinoNotificationOverdue).ToList();

        /// <summary>The pushes of the alerts of the 24 hours that go to the hosts of the flat <paramref name="propertyId"/>, in the order they were queued.</summary>
        private List<QueuedPushRecord> AlertPushesOf(Guid propertyId) =>
            AlertPushes().Where(p => p.Audience.Equals(PushAudience.PropertyHosts(propertyId))).ToList();

        /// <summary>What the families type on the page of the check-in, in one text: the control that every term of <see cref="GuestData"/> finds it.</summary>
        private const string TypedByTheGuests =
            "Hans Muster (hans.muster@ospiti-test.ch), passport X1234567; Erika Muster, identity card S1234567; Lea Muster; Bahnhofstrasse 12, 8001 Zürich; plate ZH123456; "
            + "Mirko Bernasconi (mirko.bernasconi@ospiti-test.ch)";

        /// <summary>A term found anywhere in a text, in any case.</summary>
        private static Regex Part(string text) => new(Regex.Escape(text), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>A term found only as a whole word, in any case.</summary>
        private static Regex Word(string text) => new($@"\b{Regex.Escape(text)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The file of the arrival day as the portal wants it, written here from the facts of the check-in and not by the code under test: the header of the model, the head of the family
        /// with the girl counted in «Min14» and the plate of the car, the wife with her identity card, the booking code in «Numero bollettino», dates as <c>dd.MM.yyyy</c>, countries as
        /// ISO alpha-3, lines ended by CRLF and none after the last.
        /// </summary>
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
