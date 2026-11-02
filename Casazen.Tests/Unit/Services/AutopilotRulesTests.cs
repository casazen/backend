using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The pure rules of «Gestisci con AI»: who can be reached and how, in which language, what a chat message may say, and what the assistant
/// does with each kind of item. Nothing here touches a database or a clock: the same facts always give the same answer.
/// </summary>
public class AutopilotRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Guest GuestWith(string email = "anna@example.com", string phone = "", string country = "") => new()
    {
        FirstName = "Anna",
        LastName = "Verdi",
        Email = email,
        PhoneNumber = phone,
        Country = country,
    };

    // ─── Contacts ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("anna@example.com", true)]
    [InlineData("  anna.verdi@mail.example.co.uk ", true)]
    [InlineData("anna@example", false)]
    [InlineData("anna@@example.com", false)]
    [InlineData("anna example@x.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUsableEmail_OnlyAnAddressThatLooksLikeOne(string? email, bool expected) =>
        Assert.Equal(expected, AutopilotContacts.IsUsableEmail(email));

    [Theory]
    [InlineData("+39 333 123 4567", "", "393331234567")]
    [InlineData("0039 333 123 4567", "Germany", "393331234567")]
    [InlineData("+49 151 2345 6789", "Germany", "4915123456789")]
    [InlineData("333 123 4567", "", "393331234567")]
    [InlineData("333-1234567", "Italia", "393331234567")]
    [InlineData("333 1234567", "IT", "393331234567")]
    public void InternationalPhone_WritesTheNumberTheWayWhatsAppWantsIt(string phone, string country, string expected) =>
        Assert.Equal(expected, AutopilotContacts.InternationalPhone(phone, country));

    [Theory]
    [InlineData("333 1234567", "Germany")]
    [InlineData("0151 2345678", "")]
    [InlineData("06 1234567", "")]
    [InlineData("+39", "")]
    [InlineData("+3933", "")]
    [InlineData("call me", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void InternationalPhone_WithoutAPrefixItCanTrust_IsNull(string? phone, string country) =>
        Assert.Null(AutopilotContacts.InternationalPhone(phone, country));

    [Fact]
    public void ChatLinks_CarryTheTextEncoded()
    {
        const string text = "Ciao Anna, ecco il link: https://x.example/c?a=1&b=2";

        var whatsApp = AutopilotContacts.WhatsAppUrl("393331234567", text);
        var sms = AutopilotContacts.SmsUrl("393331234567", text);

        Assert.StartsWith("https://wa.me/393331234567?text=", whatsApp, StringComparison.Ordinal);
        Assert.StartsWith("sms:+393331234567?&body=", sms, StringComparison.Ordinal);
        Assert.Equal(text, Uri.UnescapeDataString(whatsApp["https://wa.me/393331234567?text=".Length..]));
        Assert.Equal(text, Uri.UnescapeDataString(sms["sms:+393331234567?&body=".Length..]));
        Assert.DoesNotContain(" ", whatsApp, StringComparison.Ordinal);
        Assert.DoesNotContain("&b=2", whatsApp, StringComparison.Ordinal);
    }

    // ─── Language ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Germany", "", "de")]
    [InlineData("Deutschland", "", "de")]
    [InlineData("AT", "", "de")]
    [InlineData("France", "", "fr")]
    [InlineData("Spagna", "", "es")]
    [InlineData("España", "", "es")]
    [InlineData("United Kingdom", "", "en")]
    [InlineData("", "tedesca", "de")]
    [InlineData("", "Italiana", "it")]
    [InlineData("Italia", "", "it")]
    [InlineData("France", "Spanish", "fr")]
    [InlineData("", "", "it")]
    [InlineData("  ", null, "it")]
    [InlineData("Brasile", "", "en")]
    [InlineData("", "Giapponese", "en")]
    public void LanguageOf_TheGuestsOwnWhenTheBookingSaysWhereHeComesFrom(string? country, string? nationality, string expected) =>
        Assert.Equal(expected, AutopilotLanguages.Of(country, nationality));

    // ─── The text of a chat message ──────────────────────────────────────────────

    [Fact]
    public void Check_AGoodTextWithTheFourPlaceholders_IsKept()
    {
        const string good = "Ciao {nome}, per registrare il tuo soggiorno a {struttura} (arrivo del {data}) servono i dati di tutti gli ospiti. Bastano pochi minuti: {link}";

        Assert.Equal(good, AutopilotChatText.Check(good));
    }

    [Fact]
    public void Check_WithoutTheLink_ItIsPutAtTheEnd()
    {
        var checkedText = AutopilotChatText.Check("Ciao {nome}, per registrare il soggiorno servono i dati di tutti gli ospiti, con un documento.");

        Assert.NotNull(checkedText);
        Assert.EndsWith(" {link}", checkedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_WhiteSpaceAndLineBreaks_BecomeASingleSpace()
    {
        var checkedText = AutopilotChatText.Check("Ciao  {nome},\n\nper registrare il soggiorno servono i dati\tdi tutti gli ospiti.   Grazie! {link}");

        Assert.Equal("Ciao {nome}, per registrare il soggiorno servono i dati di tutti gli ospiti. Grazie! {link}", checkedText);
    }

    [Theory]
    [InlineData("Ciao {nome}, ti aspettiamo il 12 novembre: per registrare il soggiorno servono i dati degli ospiti. {link}")]
    [InlineData("Ciao {nome}, per registrare il soggiorno servono i dati degli ospiti qui https://example.com/checkin {link}")]
    [InlineData("Ciao {nome}, per registrare il soggiorno servono i dati degli ospiti: scrivimi a host@example.com {link}")]
    [InlineData("Ciao {nome}, per registrare il soggiorno servono i dati degli ospiti: www.example.com {link}")]
    [InlineData("Ciao {nome}, per registrare il soggiorno servono i dati degli ospiti, altrimenti c'è una multa {link}")]
    [InlineData("Ciao {nome}, la Questura ci chiede i dati degli ospiti per registrare il soggiorno {link}")]
    [InlineData("Ciao {nome}, per legge servono i dati: rischi una sanzione se non li mandi {link}")]
    [InlineData("Hi {nome}, the police needs the details of all guests to register the stay at {struttura} {link}")]
    [InlineData("Ciao {nome}, per registrare il soggiorno servono i dati degli ospiti, **subito** {link}")]
    [InlineData("Ciao {nome} {cognome}, per registrare il soggiorno servono i dati degli ospiti {link}")]
    [InlineData("Ciao {nome}, per registrare il soggiorno servono i dati degli ospiti {link} oppure {link}")]
    [InlineData("Ciao {nome, per registrare il soggiorno servono i dati degli ospiti {link}")]
    [InlineData("Ciao {nome}, per registrare il soggiorno servono i dati degli ospiti <b>{link}</b>")]
    [InlineData("Ciao!")]
    [InlineData("")]
    [InlineData(null)]
    public void Check_WhatAMessageToAGuestNeverHolds_IsRefused(string? text) =>
        Assert.Null(AutopilotChatText.Check(text));

    [Fact]
    public void Check_ATextThatIsTooLong_IsRefused() =>
        Assert.Null(AutopilotChatText.Check("Ciao {nome}, " + new string('a', AutopilotChatText.MaxLength) + " {link}"));

    [Fact]
    public void Fallback_EveryPlainText_PassesTheSameChecksAsTheOnesOfTheAi()
    {
        foreach (var language in AutopilotLanguages.Supported)
        {
            for (var reminder = 1; reminder <= AutopilotLimits.MaxRequestsPerStay; reminder++)
            {
                var plain = AutopilotChatText.Fallback(language, reminder);

                Assert.Equal(plain, AutopilotChatText.Check(plain));
                Assert.Contains("{link}", plain, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("xx", 1, "Hi {nome}")]
    [InlineData("it", 0, "Ciao {nome}")]
    [InlineData("it", 9, "Ciao {nome}, ultimo promemoria")]
    public void Fallback_AnUnknownLanguageIsEnglish_AndAReminderOutOfRangeIsTheNearestOne(string language, int reminder, string start) =>
        Assert.StartsWith(start, AutopilotChatText.Fallback(language, reminder), StringComparison.Ordinal);

    [Fact]
    public void Fill_PutsTheGuestThePropertyTheDayAndTheLinkInTheirPlaces()
    {
        var text = AutopilotChatText.Fill(
            "Ciao {nome}, per registrare il soggiorno a {struttura} (arrivo del {data}): {link}",
            "Anna",
            "Casa Rossi",
            new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc),
            "https://app.example/checkin/abc");

        Assert.Equal("Ciao Anna, per registrare il soggiorno a Casa Rossi (arrivo del 07/11/2026): https://app.example/checkin/abc", text);
    }

    [Fact]
    public void Fill_WithoutAFirstName_TheGreetingLosesItAndNoDoubleSpaceIsLeft()
    {
        var text = AutopilotChatText.Fill("Ciao {nome}, servono i dati: {link}", "  ", "Casa", Now, "https://app.example/c/x");

        Assert.Equal("Ciao, servono i dati: https://app.example/c/x", text);
    }

    [Fact]
    public void Fill_ANameThatLooksLikeAPlaceholder_CannotBecomeOne()
    {
        var text = AutopilotChatText.Fill("Ciao {nome}, servono i dati: {link}", "{link}", "{data}", Now, "https://app.example/c/x");

        Assert.Equal("Ciao link, servono i dati: https://app.example/c/x", text);
    }

    // ─── What the assistant does with the data of the guests ─────────────────────

    [Fact]
    public void GuestData_NeverAsked_AGuestWithAnEmailAndANumber_IsReadyByBothWays()
    {
        var (status, reason, channels) = AutopilotDecisions.GuestData(
            true, 0, null, false, GuestWith(phone: "+39 333 123 4567"), emailConfigured: true, Now);

        Assert.Equal(AutopilotItemStatus.Ready, status);
        Assert.Equal(AutopilotReasons.AskGuests, reason);
        Assert.Equal(new[] { AutopilotChannels.Email, AutopilotChannels.Chat }, channels);
    }

    [Fact]
    public void GuestData_WhenCasazenCannotSendEmail_TheChatIsTheWay()
    {
        var (status, _, channels) = AutopilotDecisions.GuestData(
            true, 0, null, false, GuestWith(phone: "+39 333 123 4567"), emailConfigured: false, Now);

        Assert.Equal(AutopilotItemStatus.Ready, status);
        Assert.Equal(new[] { AutopilotChannels.Chat }, channels);
    }

    [Fact]
    public void GuestData_NoWayToReachTheGuest_IsTheHosts()
    {
        var (status, reason, channels) = AutopilotDecisions.GuestData(
            true, 0, null, false, GuestWith(email: "not-an-email"), emailConfigured: true, Now);

        Assert.Equal(AutopilotItemStatus.Host, status);
        Assert.Equal(AutopilotReasons.NoContact, reason);
        Assert.Empty(channels);
    }

    [Fact]
    public void GuestData_AnEmailAndNoProviderToSendIt_AndNoNumber_IsTheHosts()
    {
        var (status, reason, _) = AutopilotDecisions.GuestData(true, 0, null, false, GuestWith(), emailConfigured: false, Now);

        Assert.Equal(AutopilotItemStatus.Host, status);
        Assert.Equal(AutopilotReasons.NoContact, reason);
    }

    [Fact]
    public void GuestData_AskedLessThanADayAgo_Waits_AndMoreThanADayAgo_AsksAgain()
    {
        var recent = AutopilotDecisions.GuestData(true, 1, Now.AddHours(-23), false, GuestWith(), true, Now);
        var due = AutopilotDecisions.GuestData(true, 1, Now.AddHours(-24), false, GuestWith(), true, Now);

        Assert.Equal((AutopilotItemStatus.Waiting, AutopilotReasons.AskedRecently), (recent.Status, recent.Reason));
        Assert.Equal((AutopilotItemStatus.Ready, AutopilotReasons.AskGuests), (due.Status, due.Reason));
    }

    [Fact]
    public void GuestData_ThreeRequestsMade_IsTheHostsToChase()
    {
        var (status, reason, _) = AutopilotDecisions.GuestData(
            true, AutopilotLimits.MaxRequestsPerStay, Now.AddDays(-10), false, GuestWith(), true, Now);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.AskedEnough), (status, reason));
    }

    [Fact]
    public void GuestData_TheGuestHasSubmittedAlready_AskingAgainChangesNothing()
    {
        var (status, reason, _) = AutopilotDecisions.GuestData(true, 1, Now.AddDays(-3), completed: true, GuestWith(), true, Now);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.AlreadySubmitted), (status, reason));
    }

    [Fact]
    public void GuestData_WithoutThePermission_NothingElseIsLookedAt()
    {
        var (status, reason, channels) = AutopilotDecisions.GuestData(false, 0, null, false, GuestWith(), true, Now);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.NoPermission), (status, reason));
        Assert.Empty(channels);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(9, 3)]
    public void ReminderOf_IsTheNextMessage_NeverMoreThanTheLast(int already, int expected) =>
        Assert.Equal(expected, AutopilotDecisions.ReminderOf(already));

    // ─── What the assistant does with the departures ─────────────────────────────

    private static readonly DateTime Today = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Departure_OfAStayThatIsOver_IsClosed()
    {
        var (status, reason) = AutopilotDecisions.Departure(true, BookingStatus.CheckedIn, Today.AddDays(-1), Today, null);

        Assert.Equal((AutopilotItemStatus.Ready, AutopilotReasons.CloseDeparture), (status, reason));
    }

    [Fact]
    public void Departure_Today_IsLeftToTheHost_ForTheGuestMayStillBeThere()
    {
        var (status, reason) = AutopilotDecisions.Departure(true, BookingStatus.CheckedIn, Today, Today, null);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.DepartureToday), (status, reason));
    }

    [Fact]
    public void Departure_OfABookingWhoseArrivalWasNeverRegistered_NeedsTheHostToSayTheGuestCame()
    {
        var (status, reason) = AutopilotDecisions.Departure(true, BookingStatus.Confirmed, Today, Today, null);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.ArrivalNotRegistered), (status, reason));
    }

    [Theory]
    [InlineData(AutopilotItemStatus.Ready)]
    [InlineData(AutopilotItemStatus.Waiting)]
    public void Departure_WhileTheGuestsAreBeingAskedForTheirData_Waits(AutopilotItemStatus guestData)
    {
        var (status, reason) = AutopilotDecisions.Departure(true, BookingStatus.CheckedIn, Today.AddDays(-4), Today, guestData);

        Assert.Equal((AutopilotItemStatus.Waiting, AutopilotReasons.AwaitingGuestData), (status, reason));
    }

    [Fact]
    public void Departure_WhenTheGuestsCannotBeAskedAnyMore_IsClosedAnyway()
    {
        var (status, reason) = AutopilotDecisions.Departure(true, BookingStatus.CheckedIn, Today.AddDays(-4), Today, AutopilotItemStatus.Host);

        Assert.Equal((AutopilotItemStatus.Ready, AutopilotReasons.CloseDeparture), (status, reason));
    }

    [Fact]
    public void Departure_WithoutThePermission_IsTheHosts()
    {
        var (status, reason) = AutopilotDecisions.Departure(false, BookingStatus.CheckedIn, Today.AddDays(-1), Today, null);

        Assert.Equal((AutopilotItemStatus.Host, AutopilotReasons.NoPermission), (status, reason));
    }

    // ─── What the assistant says of an Alloggiati communication ──────────────────

    private static AlloggiatiGuestSummaryInfo Summary(bool dataComplete, bool exportReady, int stayDays) => new(
        Guid.NewGuid(),
        AlloggiatiWebStatus.DaInviareManualmente,
        Today,
        stayDays,
        stayDays > 30,
        2,
        [],
        dataComplete,
        exportReady,
        []);

    [Theory]
    [InlineData(false, false, 3, AutopilotReasons.DataMissing)]
    [InlineData(true, false, 3, AutopilotReasons.CodesToComplete)]
    [InlineData(true, true, 3, AutopilotReasons.ReadyToUpload)]
    [InlineData(true, true, 30, AutopilotReasons.ReadyToUpload)]
    [InlineData(true, true, 31, AutopilotReasons.StayDaysInvalid)]
    [InlineData(true, true, 0, AutopilotReasons.StayDaysInvalid)]
    public void Alloggiati_SaysWhetherTheDataAreReadyForThePortal_AndWhatStandsInTheWay(
        bool dataComplete,
        bool exportReady,
        int stayDays,
        string expected) =>
        Assert.Equal(expected, AutopilotDecisions.Alloggiati(Summary(dataComplete, exportReady, stayDays)));
}
