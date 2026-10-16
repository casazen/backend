using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-04e: the words of the inbox that are new: the tabs <c>da-rispondere</c> and <c>in-attesa</c> (halves of <c>nuove</c>, which stays
/// what it was), the <c>source</c> filter and the limits of the free text <c>q</c>.
/// </summary>
public class SupplierInboxQueryTests
{
    [Theory]
    [InlineData("da-rispondere", SupplierInboxTab.ToAnswer)]
    [InlineData("Da-Rispondere", SupplierInboxTab.ToAnswer)]
    [InlineData("da_rispondere", SupplierInboxTab.ToAnswer)]
    [InlineData(" in-attesa ", SupplierInboxTab.AwaitingCustomer)]
    [InlineData("IN_ATTESA", SupplierInboxTab.AwaitingCustomer)]
    [InlineData("nuove", SupplierInboxTab.New)]
    public void Tabs_TryParse_TheNewValuesAndTheOldOnes(string value, SupplierInboxTab expected)
    {
        Assert.True(SupplierInboxTabs.TryParse(value, out var tab));
        Assert.Equal(expected, tab);
    }

    [Theory]
    [InlineData("rispondere")]
    [InlineData("attesa")]
    [InlineData("da-rispondere,in-attesa")]
    [InlineData("6")]
    public void Tabs_TryParse_AnythingElse_IsRefused(string value)
    {
        Assert.False(SupplierInboxTabs.TryParse(value, out _));
    }

    [Fact]
    public void Tabs_ToAnswerAndAwaitingCustomer_SplitTheNewRequestsInTwo()
    {
        // Same statuses and the same order as "nuove"; they differ only in what the supplier already did about the request.
        Assert.Equal(SupplierInboxTabs.StatusesOf(SupplierInboxTab.New), SupplierInboxTabs.StatusesOf(SupplierInboxTab.ToAnswer));
        Assert.Equal(SupplierInboxTabs.StatusesOf(SupplierInboxTab.New), SupplierInboxTabs.StatusesOf(SupplierInboxTab.AwaitingCustomer));
        Assert.Equal(SupplierInboxSort.Urgency, SupplierInboxTabs.SortOf(SupplierInboxTab.ToAnswer));
        Assert.Equal(SupplierInboxSort.Urgency, SupplierInboxTabs.SortOf(SupplierInboxTab.AwaitingCustomer));

        Assert.False(SupplierInboxTabs.AwaitingCustomerOf(SupplierInboxTab.ToAnswer));
        Assert.True(SupplierInboxTabs.AwaitingCustomerOf(SupplierInboxTab.AwaitingCustomer));
    }

    [Theory]
    [InlineData(SupplierInboxTab.New)]
    [InlineData(SupplierInboxTab.Scheduled)]
    [InlineData(SupplierInboxTab.ToCollect)]
    [InlineData(SupplierInboxTab.Archive)]
    public void Tabs_TheOtherTabs_DoNotLookAtWhatTheSupplierDid(SupplierInboxTab tab)
    {
        Assert.Null(SupplierInboxTabs.AwaitingCustomerOf(tab));
    }

    [Theory]
    [InlineData("casazen", ServiceRequestSource.Host)]
    [InlineData("CasaZen", ServiceRequestSource.Host)]
    [InlineData(" showcase ", ServiceRequestSource.Showcase)]
    [InlineData("SHOWCASE", ServiceRequestSource.Showcase)]
    public void Sources_TryParse_KnownValue_ReturnsTheOrigin(string value, ServiceRequestSource expected)
    {
        Assert.True(SupplierRequestSources.TryParse(value, out var source));
        Assert.Equal(expected, source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("host")]
    [InlineData("Host")]
    [InlineData("0")]
    [InlineData("casazen,showcase")]
    public void Sources_TryParse_AnythingElse_IsRefused_TheEnumNamesAndNumbersIncluded(string? value)
    {
        Assert.False(SupplierRequestSources.TryParse(value, out _));
    }

    [Fact]
    public void Sources_TryParse_IsTheInverseOfOf()
    {
        foreach (var source in Enum.GetValues<ServiceRequestSource>())
        {
            Assert.True(SupplierRequestSources.TryParse(SupplierRequestSources.Of(source), out var parsed));
            Assert.Equal(source, parsed);
        }
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("   ", true, null)]
    [InlineData("ab", true, "ab")]
    [InlineData("  pulizia  ", true, "pulizia")]
    [InlineData("a", false, null)]
    [InlineData(" a ", false, null)]
    public void Search_TryNormalize_NothingIsNothing_AndAShortTextIsRefused(string? value, bool valid, string? expected)
    {
        Assert.Equal(valid, SupplierInboxSearch.TryNormalize(value, out var text));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Search_TryNormalize_TheLongestTextIsSixtyCharacters()
    {
        Assert.True(SupplierInboxSearch.TryNormalize(new string('a', 60), out var text));
        Assert.Equal(60, text!.Length);
        Assert.False(SupplierInboxSearch.TryNormalize(new string('a', 61), out _));
        Assert.Equal(2, SupplierInboxSearch.MinLength);
        Assert.Equal(60, SupplierInboxSearch.MaxLength);
    }

    [Fact]
    public void Search_TryNormalize_TheLengthIsOfTheTrimmedText()
    {
        Assert.True(SupplierInboxSearch.TryNormalize("  " + new string('a', 60) + "  ", out _));
        Assert.False(SupplierInboxSearch.TryNormalize("  a  ", out _));
    }
}
