using Casazen.Core.Entities.Enums;
using Casazen.Core.GuestPortal;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

public class GuestPortalQuickRepliesTests
{
    private static GuestHouseGuide Guide => new()
    {
        CheckInHours = "15:00–20:00",
        CheckOutHours = "10:00",
        Parking = "Garage A",
        Wifi = "Private credentials",
        Access = "Private access code",
        AutoReplies = new(true, true, true, true, true),
    };

    [Theory]
    [InlineData("A che ora posso fare il check-in?", "it", KnowledgeTopic.CheckIn, "15:00–20:00", "Scrivi all’host")]
    [InlineData("What time is check-out?", "en", KnowledgeTopic.CheckOut, "10:00", "Write to the host")]
    [InlineData("¿Dónde puedo aparcar?", "es", KnowledgeTopic.Parking, "Garage A", "Escribir al anfitrión")]
    public void AnswersUseTheConfiguredGuideAndDiscloseAutomation(string question, string language, KnowledgeTopic topic, string content, string handoff)
    {
        var reply = Assert.IsType<GuestPortalQuickReply>(GuestPortalQuickReplies.Answer(Guide, question, language, false));
        Assert.Equal(topic, reply.Topic);
        Assert.Contains(content, reply.Text, StringComparison.Ordinal);
        Assert.Contains(handoff, reply.Text, StringComparison.Ordinal);
        Assert.Contains(language == "es" ? "automát" : "automat", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("What time is check-in? I want a refund.")]
    [InlineData("What time is check-in? Here is my phone number")]
    [InlineData("What time is check-in tomorrow for my booking?")]
    [InlineData("What is the door code?")]
    public void UnrecognizedOrCombinedQuestionsStayWithTheHost(string question)
    {
        Assert.Null(GuestPortalQuickReplies.Answer(Guide, question, "en", true));
    }

    [Fact]
    public void WifiAlwaysUsesTheProtectedGuideAndRequiresAccess()
    {
        const string question = "What is the Wi-Fi password?";
        Assert.Null(GuestPortalQuickReplies.Answer(Guide, question, "en", false));
        var reply = Assert.IsType<GuestPortalQuickReply>(GuestPortalQuickReplies.Answer(Guide, question, "en", true));
        Assert.Contains("Wi-Fi section", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Guide.Wifi!, reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Guide.Access!, reply.Text, StringComparison.Ordinal);
        Assert.Null(Guide.ForVisitor(true).AutoReplies);
    }

    [Fact]
    public void RequiresPropertyOptInAndPublishedTopic()
    {
        const string question = "Where can I park?";
        Assert.Null(GuestPortalQuickReplies.Answer(Guide with { AutoReplies = null }, question, "en", true));
        Assert.Null(GuestPortalQuickReplies.Answer(Guide with { AutoReplies = Guide.AutoReplies! with { Parking = false } }, question, "en", true));
        Assert.Null(GuestPortalQuickReplies.Answer(Guide with { Parking = " " }, question, "en", true));
    }
}
