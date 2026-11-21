using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Regulatory.GuestRegistration;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// E31/F02/US01/T01 (NF-131, decisions D1, D5 and D12): the pure rules of the assisted notification of the guests to the authority of a canton other than Ticino: which regimes it covers, when
/// the notification of an arrival day is due on the clock of the property (the three kinds of deadline of the brief, and no deadline when the regime has none), and the guests of a day as data
/// ready to copy with what they lack. No database, no clock.
/// </summary>
public class SwissAssistedNotificationTests
{
    private static readonly TimeZoneInfo Zurich = RomeCalendar.ZoneOf("Europe/Zurich");

    private static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static GuestRegistrationRegime Regime(string canton) => GuestRegistrationRegimeCatalog.ForCanton(canton);

    // ─── Which regimes ───

    [Fact]
    public void Covers_TheNineCantonsThatAskForAnElectronicNotification_AndNoOtherRegime()
    {
        // A photograph of the catalog: a canton added to or taken out of the assisted notification is a decision somebody makes on purpose.
        var covered = GuestRegistrationRegimeCatalog.All.Where(SwissAssistedNotification.Covers).Select(r => r.Code).Order(StringComparer.Ordinal);

        Assert.Equal(["CH-BS", "CH-FR", "CH-GE", "CH-NE", "CH-NW", "CH-SH", "CH-TG", "CH-ZG", "CH-ZH"], covered);
        Assert.All(GuestRegistrationRegimeCatalog.All.Where(SwissAssistedNotification.Covers), r =>
        {
            Assert.Equal(GuestRegistrationObligation.ReportToAuthority, r.Obligation);
            Assert.Equal(GuestRegistrationSupport.AssistedNotification, r.CasaZenSupport);
        });
    }

    [Fact]
    public void Covers_NeverTicino_NeverItaly_NeverTheUnknownPlace_NeverACantonThatOnlyKeepsARegister()
    {
        Assert.False(SwissAssistedNotification.Covers(GuestRegistrationRegimeCatalog.Find(GuestRegistrationRegimeCatalog.TicinoCode)));
        Assert.False(SwissAssistedNotification.Covers(GuestRegistrationRegimeCatalog.Italian));
        Assert.False(SwissAssistedNotification.Covers(GuestRegistrationRegimeCatalog.UnknownSwissPlace));
        Assert.False(SwissAssistedNotification.Covers(null));

        // Bern, Vaud and Appenzell Innerrhoden ask the host to keep a register: whatever the host says, there is nothing to notify.
        foreach (var canton in new[] { "BE", "VD", "AI", "LU" })
            Assert.False(SwissAssistedNotification.Covers(Regime(canton)), canton);
    }

    // ─── The deadline ───

    [Theory]
    [InlineData("2026-11-19", "2026-11-20T01:00:00Z")] // 02:00 CET the night after
    [InlineData("2026-07-15", "2026-07-16T00:00:00Z")] // 02:00 CEST the night after
    [InlineData("2026-03-28", "2026-03-29T01:00:00Z")] // 02:00 does not exist the night the clocks go forward: read one hour later on the clock (03:00 CEST)
    [InlineData("2026-10-24", "2026-10-25T00:00:00Z")] // 02:00 happens twice the night they go back: the first one, in summer time
    public void DueAtUtc_DailyByTime_IsTheNightAfterTheArrivalAtTheTimeOfTheRegime_OnTheClockOfTheProperty(string arrival, string dueUtc)
    {
        var schaffhausen = Regime("SH");
        Assert.Equal(new TimeOnly(2, 0), schaffhausen.DeadlineTime);

        var due = SwissAssistedNotification.DueAtUtc(schaffhausen, DateOnly.Parse(arrival, CultureInfo.InvariantCulture), Zurich, null);

        Assert.Equal(Utc(dueUtc), due);
        Assert.Equal(DateTimeKind.Utc, due!.Value.Kind);
    }

    [Theory]
    [InlineData("BS")]
    [InlineData("NE")]
    public void DueAtUtc_DailyByTimeWithNoTimeInTheSource_IsTheEndOfTheArrivalDay_NeverATimeNobodyWrote(string canton)
    {
        var regime = Regime(canton);
        Assert.Equal(GuestRegistrationDeadlineKind.DailyByTime, regime.DeadlineKind);
        Assert.Null(regime.DeadlineTime);

        // The end of the arrival day on the clock of the property: midnight of the next day, 23:00Z in winter.
        Assert.Equal(
            Utc("2026-11-19T23:00:00Z"),
            SwissAssistedNotification.DueAtUtc(regime, new DateOnly(2026, 11, 19), Zurich, null));
        Assert.Equal(
            Utc("2026-07-15T22:00:00Z"),
            SwissAssistedNotification.DueAtUtc(regime, new DateOnly(2026, 7, 15), Zurich, null));
    }

    [Fact]
    public void TheTimeOfTheRegime_IsInTheCatalogOnlyWhereTheSourceGivesOne()
    {
        // Only Schaffhausen has a time (the brochure says «by 02:00»), and only a daily regime reads it.
        var withATime = GuestRegistrationRegimeCatalog.All.Where(r => r.DeadlineTime is not null).Select(r => r.Code).ToList();

        Assert.Equal(["CH-SH"], withATime);
        Assert.All(GuestRegistrationRegimeCatalog.All.Where(r => r.DeadlineTime is not null), r => Assert.Equal(GuestRegistrationDeadlineKind.DailyByTime, r.DeadlineKind));
    }

    [Theory]
    [InlineData("2026-11-16", "2026-11-22T22:59:00Z")] // Monday: the Sunday of the week, 23:59 CET
    [InlineData("2026-11-19", "2026-11-22T22:59:00Z")] // Thursday
    [InlineData("2026-11-21", "2026-11-22T22:59:00Z")] // Saturday
    [InlineData("2026-11-22", "2026-11-22T22:59:00Z")] // Sunday is its own week's last day
    [InlineData("2026-07-15", "2026-07-19T21:59:00Z")] // Wednesday in summer time: 23:59 CEST
    [InlineData("2026-03-23", "2026-03-29T21:59:00Z")] // the week the clocks go forward: Sunday 23:59 is already CEST
    [InlineData("2026-10-19", "2026-10-25T22:59:00Z")] // the week they go back: Sunday 23:59 is CET
    public void DueAtUtc_Weekly_IsSundayAt2359OfTheArrivalWeek_OnTheClockOfTheProperty(string arrival, string dueUtc)
    {
        var nidwalden = Regime("NW");
        Assert.Equal(GuestRegistrationDeadlineKind.Weekly, nidwalden.DeadlineKind);

        Assert.Equal(Utc(dueUtc), SwissAssistedNotification.DueAtUtc(nidwalden, DateOnly.Parse(arrival, CultureInfo.InvariantCulture), Zurich, null));
    }

    [Fact]
    public void SundayOf_EveryDayOfAWeek_IsTheSundayThatEndsIt()
    {
        var monday = new DateOnly(2026, 11, 16);

        for (var offset = 0; offset < 7; offset++)
            Assert.Equal(new DateOnly(2026, 11, 22), SwissAssistedNotification.SundayOf(monday.AddDays(offset)));

        Assert.Equal(new DateOnly(2026, 11, 29), SwissAssistedNotification.SundayOf(new DateOnly(2026, 11, 23)));
    }

    [Theory]
    [InlineData("ZG")]
    [InlineData("GE")]
    public void DueAtUtc_AtArrival_IsTheFirstArrivalRecorded_ElseTheEndOfTheArrivalDay(string canton)
    {
        var regime = Regime(canton);
        Assert.Equal(GuestRegistrationDeadlineKind.AtArrival, regime.DeadlineKind);
        var day = new DateOnly(2026, 11, 19);

        Assert.Equal(Utc("2026-11-19T14:00:00Z"), SwissAssistedNotification.DueAtUtc(regime, day, Zurich, Utc("2026-11-19T14:00:00Z")));

        // An arrival that came back from the database as an unspecified kind is read as UTC, like everywhere else.
        var unspecified = DateTime.SpecifyKind(new DateTime(2026, 11, 19, 14, 0, 0), DateTimeKind.Unspecified);
        Assert.Equal(Utc("2026-11-19T14:00:00Z"), SwissAssistedNotification.DueAtUtc(regime, day, Zurich, unspecified));

        // No arrival recorded yet: the guests are expected during the day, so the day ends the notification (never a moment nobody knows).
        Assert.Equal(Utc("2026-11-19T23:00:00Z"), SwissAssistedNotification.DueAtUtc(regime, day, Zurich, null));
    }

    [Theory]
    [InlineData("FR")]
    [InlineData("TG")]
    [InlineData("ZH")]
    public void DueAtUtc_ARegimeWithNoDeadline_HasNone_AGuessIsNeverMadeUp(string canton)
    {
        var regime = Regime(canton);
        Assert.Equal(GuestRegistrationDeadlineKind.None, regime.DeadlineKind);

        Assert.Null(SwissAssistedNotification.DueAtUtc(regime, new DateOnly(2026, 11, 19), Zurich, Utc("2026-11-19T14:00:00Z")));
    }

    [Fact]
    public void DueAtUtc_TheClockOfTheProperty_IsTheOneOfItsOwnZone()
    {
        // Midnight of the end of the day on a property whose clock is UTC is 00:00Z, not the 23:00Z of Zurich in winter.
        Assert.Equal(
            Utc("2026-11-20T00:00:00Z"),
            SwissAssistedNotification.DueAtUtc(Regime("BS"), new DateOnly(2026, 11, 19), TimeZoneInfo.Utc, null));
    }

    [Fact]
    public void DueAtUtc_Hours24FromArrival_IsNotUsedByACoveredRegime_AndIsTheRuleOfTicinoWhenAsked()
    {
        // Ticino and Italy have their own flows; the rule is total all the same: 24 hours from the first arrival recorded, else the end of the arrival day.
        Assert.DoesNotContain(
            GuestRegistrationRegimeCatalog.All.Where(SwissAssistedNotification.Covers),
            r => r.DeadlineKind == GuestRegistrationDeadlineKind.Hours24FromArrival);

        var ticino = GuestRegistrationRegimeCatalog.Find(GuestRegistrationRegimeCatalog.TicinoCode)!;
        Assert.Equal(
            Utc("2026-11-20T14:00:00Z"),
            SwissAssistedNotification.DueAtUtc(ticino, new DateOnly(2026, 11, 19), Zurich, Utc("2026-11-19T14:00:00Z")));
        Assert.Equal(
            Utc("2026-11-19T23:00:00Z"),
            SwissAssistedNotification.DueAtUtc(ticino, new DateOnly(2026, 11, 19), Zurich, null));
    }

    // ─── The guests of a day ───

    private static readonly DateOnly Arrival = new(2026, 11, 19);
    private static readonly DateOnly Today = new(2026, 11, 19);

    private static TicinoGuestFacts Guest(
        int position = 0,
        string? first = "Anna",
        string? last = "Rossi",
        DateOnly? born = null,
        string? nationalityCode = "100000241",
        string? nationality = "SVIZZERA",
        string? documentTypeCode = "PASOR",
        string? documentNumber = "X1234567",
        GuestDocumentType? documentType = null,
        bool unknownBirth = false) => new(
        position,
        StayGuestType.SingleGuest,
        first,
        last,
        unknownBirth ? null : born ?? new DateOnly(1984, 1, 4),
        nationalityCode,
        nationality,
        false,
        string.Empty,
        "Zurigo",
        documentType,
        documentTypeCode,
        documentNumber,
        false,
        null,
        "100000241",
        "SVIZZERA",
        "Via Berna 11",
        "8001",
        "Zürich");

    private static TicinoStayFacts Stay(IReadOnlyList<TicinoGuestFacts> guests, int numberOfGuests = 1, string? bookingCode = "7Q3KZ-8M2XB", string? plate = "ZH 123456") => new(
        Guid.NewGuid(),
        "Anna Rossi",
        Arrival,
        Arrival.AddDays(2),
        numberOfGuests,
        guests,
        bookingCode,
        plate);

    [Fact]
    public void Plan_ACompleteGuest_IsCopyReady_WithNothingMissing()
    {
        var stay = Stay([Guest()]);

        var (guests, issues) = SwissAssistedNotification.Plan([stay], Today);

        Assert.Empty(issues);
        var guest = Assert.Single(guests);
        Assert.Equal(
            (stay.BookingId, "7Q3KZ-8M2XB", 0, Arrival, Arrival.AddDays(2), "Anna", "Rossi", new DateOnly(1984, 1, 4)),
            (guest.BookingId, guest.BookingCode, guest.Position, guest.Arrival, guest.Departure, guest.FirstName, guest.LastName, guest.BirthDate));
        Assert.Equal(("Zurigo", "SVIZZERA", "CHE", SwissDocumentTypes.Passport, "X1234567"), (guest.PlaceOfBirth, guest.Nationality, guest.NationalityCode, guest.DocumentType, guest.DocumentNumber));
        Assert.Equal(("Via Berna 11", "8001", "Zürich", "CHE", "ZH 123456"), (guest.Street, guest.PostalCode, guest.City, guest.ResidenceCountryCode, guest.Plate));
    }

    [Fact]
    public void Plan_AGuestWithGaps_StaysInTheList_AndSaysWhatItLacks()
    {
        var stay = Stay([Guest(first: " ", last: null, unknownBirth: true, nationalityCode: null, nationality: "", documentTypeCode: null, documentNumber: null)]);

        var (guests, issues) = SwissAssistedNotification.Plan([stay], Today);

        // The host types what is there and completes the rest: the guest is listed, with the fields that are not recorded as null.
        var guest = Assert.Single(guests);
        Assert.Null(guest.FirstName);
        Assert.Null(guest.LastName);
        Assert.Null(guest.BirthDate);
        Assert.Null(guest.Nationality);
        Assert.Null(guest.NationalityCode);
        Assert.Null(guest.DocumentType);
        Assert.Null(guest.DocumentNumber);
        var issue = Assert.Single(issues);
        Assert.Equal((stay.BookingId, 0, "Anna Rossi"), (issue.BookingId, issue.Position, issue.GuestName));
        Assert.Equal(["firstName", "lastName", "birthDate", "nationality", "documentType"], issue.Missing);
    }

    [Fact]
    public void Plan_AMinorNeedsNoDocument_AnAdultAndAGuestOfUnknownAgeDo()
    {
        var minor = Guest(position: 1, first: "Luca", born: new DateOnly(2015, 6, 1), documentTypeCode: null, documentNumber: null);
        var adultWithoutDocument = Guest(position: 2, first: "Marta", born: new DateOnly(2000, 1, 1), documentTypeCode: null, documentNumber: null);
        var unknownAge = Guest(position: 3, first: "Paolo", unknownBirth: true, documentTypeCode: null, documentNumber: null);
        var stay = Stay([minor, adultWithoutDocument, unknownAge], numberOfGuests: 3);

        var (guests, issues) = SwissAssistedNotification.Plan([stay], Today);

        Assert.Equal(3, guests.Count);
        Assert.Equal([2, 3], issues.Select(i => i.Position!.Value));
        Assert.Equal(["documentType"], issues.Single(i => i.Position == 2).Missing);
        Assert.Equal(["birthDate", "documentType"], issues.Single(i => i.Position == 3).Missing);
    }

    [Fact]
    public void Plan_ABirthDateOutsideTheRange_IsMissing()
    {
        var future = Guest(position: 0, born: Today.AddDays(1));
        var ancient = Guest(position: 1, first: "Vecchio", born: new DateOnly(1899, 12, 31));
        var stay = Stay([future, ancient], numberOfGuests: 2);

        var (_, issues) = SwissAssistedNotification.Plan([stay], Today);

        Assert.All(issues, i => Assert.Contains("birthDate", i.Missing));
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void Plan_FewerGuestsThanTheBookingCounts_IsAStayLevelIssue()
    {
        var stay = Stay([Guest()], numberOfGuests: 3);
        var nobody = Stay([], numberOfGuests: 1);

        var (_, issues) = SwissAssistedNotification.Plan([stay, nobody], Today);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, i =>
        {
            Assert.Null(i.Position);
            Assert.Equal(["guests"], i.Missing);
        });
    }

    [Fact]
    public void Plan_TheNationalityTypedAsAnAlphaCode_IsEnoughAndGivesItsCode_AndAnUnknownNameIsKeptAsEntered()
    {
        var typedCode = Guest(position: 0, nationalityCode: null, nationality: "ITA");
        var unknownName = Guest(position: 1, first: "Mario", nationalityCode: null, nationality: "Atlantide");
        var stay = Stay([typedCode, unknownName], numberOfGuests: 2);

        var (guests, issues) = SwissAssistedNotification.Plan([stay], Today);

        Assert.Equal(("ITA", "CHE"), (guests[0].NationalityCode, guests[0].ResidenceCountryCode));
        Assert.Equal(("Atlantide", (string?)null), (guests[1].Nationality, guests[1].NationalityCode));
        Assert.Empty(issues);
    }

    [Fact]
    public void Plan_TheGuestsAreInTheOrderOfTheStays_ThenOfTheCheckIn_AndTheHomeInItalyIsITA()
    {
        var later = new TicinoStayFacts(Guid.NewGuid(), "Z", Arrival.AddDays(1), Arrival.AddDays(3), 1, [Guest(first: "Secondo")]);
        var first = Stay([Guest(position: 1, first: "Quarto"), Guest(position: 0, first: "Terzo") with { ResidenceInItaly = true, ResidenceComuneName = "Como" }], numberOfGuests: 2);

        var (guests, _) = SwissAssistedNotification.Plan([later, first], Today);

        Assert.Equal(["Terzo", "Quarto", "Secondo"], guests.Select(g => g.FirstName));
        Assert.Equal(("ITA", "Como"), (guests[0].ResidenceCountryCode, guests[0].City));
    }

    [Fact]
    public void TheGuestOfTheDay_HoldsNoExemptionNoImageAndNoContactOfTheBooker()
    {
        // A tripwire like the one of the tracking tables: a new member must make somebody decide again whether it is data the notification needs.
        var names = typeof(SwissNotificationGuest).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            [
                "Arrival", "BirthDate", "BookingCode", "BookingId", "City", "Departure", "DocumentNumber", "DocumentType", "FirstName", "LastName", "Nationality", "NationalityCode",
                "PlaceOfBirth", "Plate", "Position", "PostalCode", "ResidenceCountryCode", "Street",
            ],
            names);
    }
}
