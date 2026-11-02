using System.Reflection;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// The AI help of the showcase editor sits behind the same door as the rest of the editor (the supplier of the caller's own org) and under the rate
/// limit of every endpoint that calls an AI provider: the status read costs nothing and calls no provider, so it has no such limit.
/// </summary>
public class ShowcaseTextSuggestionEndpointTests
{
    private static MethodInfo Action(string name) =>
        typeof(SupplierShowcaseContentController).GetMethod(name) ?? throw new InvalidOperationException($"No action {name}.");

    [Fact]
    public void TheController_IsForSuppliersOnly()
    {
        var authorize = typeof(SupplierShowcaseContentController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(CasazenPolicies.Supplier, authorize!.Policy);
    }

    [Fact]
    public void TheSuggestion_IsAPostUnderTheAiRateLimit_WithASmallBody()
    {
        var action = Action(nameof(SupplierShowcaseContentController.SuggestTexts));

        Assert.Equal("texts/suggestion", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.NotNull(action.GetCustomAttribute<AiRateLimitAttribute>());
        Assert.NotNull(action.GetCustomAttribute<RequestSizeLimitAttribute>());
    }

    [Fact]
    public void TheStatus_IsAGetWithNoAiRateLimit()
    {
        var action = Action(nameof(SupplierShowcaseContentController.GetTextSuggestionStatus));

        Assert.Equal("texts/suggestion/status", action.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Null(action.GetCustomAttribute<AiRateLimitAttribute>());
    }

    [Fact]
    public void TheRequest_OnlyTakesWhatTheSupplierTypedAndTheMembersAsked()
    {
        var properties = typeof(SuggestShowcaseTextsRequest).GetProperties().Select(property => property.Name).Order().ToArray();

        // No supplier id, no facts: the data of the supplier is read on the server from its own org.
        Assert.Equal(new[] { "Answers", "Fields", "Regenerate" }, properties.Where(name => name is not "ToCommand").ToArray());
    }

    [Fact]
    public void TheRequest_MapsToTheCommandWithCleanedAnswers()
    {
        var request = new SuggestShowcaseTextsRequest
        {
            Fields = ["bio"],
            Answers = new ShowcaseTextAnswersRequest { About = "  Una   piccola squadra  ", Years = "   " },
            Regenerate = true,
        };

        var command = request.ToCommand();

        Assert.Equal(new[] { "bio" }, command.Fields);
        Assert.Equal("Una piccola squadra", command.Answers!.About);
        Assert.Null(command.Answers.Years);
        Assert.True(command.Regenerate);
    }
}
