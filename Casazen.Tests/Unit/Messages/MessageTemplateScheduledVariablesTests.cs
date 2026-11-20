using System.Globalization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// E05/F07/US01/T03: the five variables the scheduled messages add to the templates (the check-in method, the check-in link, the access code, the address of the property and the review link), and the list of what a
/// text still lacks (<see cref="MessageTemplateRenderer.MissingVariables"/>) that holds a message back. The composer of the hub never has the link or the code to give, so a template that names them is refused there
/// with the name of the variable, and a literal variable never reaches a guest.
/// </summary>
public class MessageTemplateScheduledVariablesTests
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it");

    private static readonly MessageTemplateValues OfAStay = new(
        "Giulia",
        "Casa Rosa (TEST)",
        "CZ-0001",
        new DateOnly(2026, 11, 20),
        new DateOnly(2026, 11, 23),
        new TimeOnly(15, 0),
        new TimeOnly(10, 0),
        "Marco (TEST)",
        CheckInMethod: "cassetta a codice (TEST)",
        PropertyAddress: "Via Roma 1, 00100 Roma");

    [Fact]
    public void TheVariables_AreThirteen_InTheOrderTheScreensListThem_TheFiveOfTheScheduledMessagesLast()
    {
        Assert.Equal(
            [
                "guest_name", "property_name", "booking_code", "check_in_date", "check_out_date", "check_in_time", "check_out_time", "host_name",
                "check_in_link", "check_in_method", "access_code", "property_address", "review_link",
            ],
            MessageTemplateRenderer.Variables);
    }

    [Fact]
    public void TheMethodAndTheAddress_AreFilledFromTheStay_AndTheLinkTheCodeAndTheReviewLinkFromWhatTheSendIssues()
    {
        var values = OfAStay with { CheckInLink = "https://example.com/checkin/TEST", AccessCode = "4321", ReviewLink = "https://example.com/review/TEST" };

        var text = MessageTemplateRenderer.Render(
            "{{property_address}}: {{check_in_method}}. Check-in {{check_in_link}}, codice {{access_code}}, recensione {{review_link}} (TEST)",
            values,
            Italian);

        Assert.Equal("Via Roma 1, 00100 Roma: cassetta a codice (TEST). Check-in https://example.com/checkin/TEST, codice 4321, recensione https://example.com/review/TEST (TEST)", text);
    }

    [Theory]
    [InlineData("{{check_in_link}}", "check_in_link")]
    [InlineData("Il codice è {{access_code}}", "access_code")]
    [InlineData("Recensione: {{review_link}}", "review_link")]
    public void ATemplateThatNamesTheLinkOrTheCode_IsRefusedWhereThereIsNone_WithTheNameOfTheVariable(string body, string name)
    {
        // The composer of the hub fills a template from the conversation only: no link of the check-in is issued and no code is released there.
        var refused = Assert.Throws<DomainRuleException>(() => MessageTemplateRenderer.Render(body, OfAStay, Italian));

        Assert.Equal(MessageTemplateRenderer.MissingCode, refused.Code);
        Assert.Equal(name, Assert.Single(refused.MessageArgs));
    }

    [Fact]
    public void WhatATextLacks_IsListedOnceEach_InTheOrderTheyFirstAppear_AndOnlyForTheVariablesItUses()
    {
        var lacking = MessageTemplateRenderer.MissingVariables(
            "{{review_link}} {{guest_name}} {{access_code}} {{review_link}} {{check_in_method}} {{check_in_link}}",
            OfAStay,
            Italian);

        Assert.Equal(["review_link", "access_code", "check_in_link"], lacking);
        Assert.Empty(MessageTemplateRenderer.MissingVariables("Ciao {{guest_name}}", OfAStay, Italian));
        Assert.Empty(MessageTemplateRenderer.MissingVariables("Nessuna variabile", OfAStay, Italian));
    }

    [Theory]
    [InlineData("Ciao {{nome}}")]
    [InlineData("Ciao {{guest_name")]
    [InlineData("Ciao guest_name}}")]
    public void ANameThatIsNotAVariable_OrABraceThatOpensNothing_IsRefusedWhenWhatATextLacksIsAsked(string body)
    {
        var refused = Assert.Throws<DomainRuleException>(() => MessageTemplateRenderer.MissingVariables(body, OfAStay, Italian));

        Assert.Equal(MessageTemplateRenderer.UnknownCode, refused.Code);
    }

    [Fact]
    public void APreview_TakesOutWhatTheStayHasNoValueFor_AndNeverLeavesAVariableInTheText()
    {
        var text = MessageTemplateRenderer.FillKnown("Ciao {{guest_name}}, il codice è {{access_code}}. Metodo: {{check_in_method}} {{review_link}}", OfAStay, Italian, out var unfilled);

        Assert.Equal("Ciao Giulia, il codice è. Metodo: cassetta a codice (TEST)", text);
        Assert.Equal(["access_code", "review_link"], unfilled);
        Assert.DoesNotContain("{{", text, StringComparison.Ordinal);
    }
}
