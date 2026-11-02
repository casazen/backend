using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-13a: what a supplier needs before it publishes its showcase, and when the showcase takes bookings. The bookings are the money
/// rule (VT-D7: they open only with the payment), so the whole truth table of <see cref="ShowcaseBookings"/> is checked.
/// </summary>
public class ShowcaseReadinessTests
{
    private static ShowcaseReadinessFacts Facts(int services = 1, bool zones = true, bool hours = true, bool verified = true) =>
        new(services, zones, hours, verified);

    [Fact]
    public void WithoutAnActiveServiceOrTheZones_ThePublicationIsRefused_WithTheCodesOfWhatIsMissing()
    {
        var result = ShowcaseReadiness.Evaluate(new ShowcaseContentDocument(), Facts(services: 0, zones: false));

        Assert.False(result.CanPublish);
        Assert.Equal(new[] { ShowcaseReadiness.Services, ShowcaseReadiness.Zones }, result.Missing);
    }

    [Fact]
    public void WhatIsOnlyAdvised_NeverBlocksThePublication_ButIsAWarning()
    {
        var result = ShowcaseReadiness.Evaluate(new ShowcaseContentDocument(), Facts(hours: false, verified: false));

        Assert.True(result.CanPublish);
        Assert.Empty(result.Missing);
        Assert.All(result.Items.Where(item => !item.Required), item => Assert.Equal(ShowcaseReadinessLevel.Warn, item.Level));
    }

    [Fact]
    public void ThePresentationPhotosAndQuestions_AreDoneFromTheRecommendedAmount()
    {
        var document = new ShowcaseContentDocument
        {
            Tagline = "Pronti in un'ora",
            Bio = new string('a', ShowcaseContentLimits.RecommendedBioMin),
            Gallery = Enumerable.Range(0, ShowcaseContentLimits.RecommendedPhotos).Select(i => new ShowcaseWork($"https://storage.test/{i}.png", null, null, null, null)).ToList(),
            Faq = Enumerable.Range(0, ShowcaseContentLimits.RecommendedFaq).Select(i => new ShowcaseFaq($"Domanda {i}", "Risposta")).ToList(),
        };

        var result = ShowcaseReadiness.Evaluate(document, Facts());

        Assert.All(result.Items, item => Assert.Equal(ShowcaseReadinessLevel.Ok, item.Level));
    }

    [Fact]
    public void TheBookings_AreOpenOnlyWhenPublishedNotPausedTheSwitchOnThePaymentsOnAndTheAccountReady()
    {
        // Every combination of the five facts (2^5 = 32).
        for (var mask = 0; mask < 32; mask++)
        {
            var published = (mask & 1) != 0;
            var paused = (mask & 2) != 0;
            var switchOn = (mask & 4) != 0;
            var paymentsOn = (mask & 8) != 0;
            var accountReady = (mask & 16) != 0;

            var bookings = ShowcaseBookings.Evaluate(published, paused, switchOn, paymentsOn, accountReady);
            var expected = published && !paused && switchOn && paymentsOn && accountReady;

            Assert.Equal(expected, bookings.Open);
            Assert.Equal(expected, bookings.BlockedBy.Count == 0);

            // The value the publication writes is the same rule without the switch itself.
            Assert.Equal(published && !paused && paymentsOn && accountReady, ShowcaseBookings.SwitchValue(published, paused, paymentsOn, accountReady));
        }
    }

    [Fact]
    public void TheBookings_SayWhyTheyAreClosed_InAStableOrder()
    {
        Assert.Equal(
            new[] { ShowcaseBookingBlocks.NotPublished, ShowcaseBookingBlocks.PaymentsOff, ShowcaseBookingBlocks.AccountNotReady },
            ShowcaseBookings.Evaluate(published: false, paused: false, onlineBookingEnabled: false, paymentsFlagOn: false, accountReady: false).BlockedBy);

        // Everything is ready but the switch: the account became ready after the publication ("Riattiva" turns it on).
        Assert.Equal(
            new[] { ShowcaseBookingBlocks.SwitchOff },
            ShowcaseBookings.Evaluate(published: true, paused: false, onlineBookingEnabled: false, paymentsFlagOn: true, accountReady: true).BlockedBy);
    }
}
