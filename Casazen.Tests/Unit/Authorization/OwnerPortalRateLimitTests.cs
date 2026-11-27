using System.Reflection;
using Casazen.Web.Controllers;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// E07/F07/US01 (NF-111): the owner portal is used by people outside the organization and its endpoints send e-mails, read files of up to 10 MB and write a line of
/// audit for every download, so every one of them has a rate limit per signed-in person, and the ones that send, store or stream a file have a tighter one.
/// </summary>
public class OwnerPortalRateLimitTests
{
    private static readonly Type[] Controllers =
    [
        typeof(OwnerPortalCalendarController),
        typeof(OwnerPortalCollaborationController),
        typeof(OwnerContactAcceptanceController),
        typeof(OwnerStaysController),
        typeof(OwnerCollaborationController),
    ];

    private static IEnumerable<MethodInfo> Actions(Type controller) =>
        controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static string? PolicyOf(MethodInfo action) =>
        (action.GetCustomAttribute<EnableRateLimitingAttribute>() ?? action.DeclaringType!.GetCustomAttribute<EnableRateLimitingAttribute>())?.PolicyName;

    private static string? PolicyOf<T>(string action) => PolicyOf(typeof(T).GetMethod(action)!);

    [Fact]
    public void EveryAction_OfTheOwnerPortalControllers_HasARegisteredPolicy_PerSignedInPerson()
    {
        var registered = RateLimitingServiceCollectionExtensions.Policies.ToDictionary(policy => policy.Name, StringComparer.Ordinal);

        foreach (var controller in Controllers)
        {
            var actions = Actions(controller).ToList();
            Assert.NotEmpty(actions);
            foreach (var action in actions)
            {
                var policy = PolicyOf(action);
                Assert.False(string.IsNullOrEmpty(policy), $"{controller.Name}.{action.Name} has no rate limit");
                Assert.True(registered.TryGetValue(policy!, out var definition), $"{controller.Name}.{action.Name}: policy {policy} is not registered");
                Assert.True(definition!.PartitionByUser, $"{controller.Name}.{action.Name}: {policy} counts the client address, an owner is counted as a person");
            }
        }
    }

    [Fact]
    public void TheInvitationTheWritesAndTheDownloads_HaveTheirOwnPolicies_TighterThanTheReads()
    {
        // Invite a contact: an e-mail to an address the owner chose.
        Assert.Equal(RateLimitPolicies.OwnerPortalInvite, PolicyOf<OwnerPortalCollaborationController>(nameof(OwnerPortalCollaborationController.InviteContact)));

        // Open a conversation, write a message with its file (the owner), and the same and the documents (the agency): an e-mail to the other side and up to 10 MB.
        Assert.Equal(RateLimitPolicies.OwnerPortalWrite, PolicyOf<OwnerPortalCollaborationController>(nameof(OwnerPortalCollaborationController.StartConversation)));
        Assert.Equal(RateLimitPolicies.OwnerPortalWrite, PolicyOf<OwnerPortalCollaborationController>(nameof(OwnerPortalCollaborationController.PostMessage)));
        Assert.Equal(RateLimitPolicies.OwnerPortalWrite, PolicyOf<OwnerCollaborationController>(nameof(OwnerCollaborationController.StartConversation)));
        Assert.Equal(RateLimitPolicies.OwnerPortalWrite, PolicyOf<OwnerCollaborationController>(nameof(OwnerCollaborationController.PostMessage)));
        Assert.Equal(RateLimitPolicies.OwnerPortalWrite, PolicyOf<OwnerCollaborationController>(nameof(OwnerCollaborationController.ShareDocument)));
        Assert.Equal(RateLimitPolicies.OwnerPortalWrite, PolicyOf<OwnerCollaborationController>(nameof(OwnerCollaborationController.AddVersion)));

        // The files: a line of audit and up to 10 MB each.
        Assert.Equal(RateLimitPolicies.OwnerPortalDownload, PolicyOf<OwnerPortalCollaborationController>(nameof(OwnerPortalCollaborationController.DownloadDocument)));
        Assert.Equal(RateLimitPolicies.OwnerPortalDownload, PolicyOf<OwnerPortalCollaborationController>(nameof(OwnerPortalCollaborationController.DownloadAttachment)));
        Assert.Equal(RateLimitPolicies.OwnerPortalDownload, PolicyOf<OwnerCollaborationController>(nameof(OwnerCollaborationController.DownloadVersion)));
        Assert.Equal(RateLimitPolicies.OwnerPortalDownload, PolicyOf<OwnerCollaborationController>(nameof(OwnerCollaborationController.DownloadAttachment)));

        // The reads and the light calls keep the default of the controller.
        Assert.Equal(RateLimitPolicies.OwnerPortalRead, PolicyOf<OwnerPortalCalendarController>(nameof(OwnerPortalCalendarController.GetCalendar)));
        Assert.Equal(RateLimitPolicies.OwnerPortalRead, PolicyOf<OwnerContactAcceptanceController>(nameof(OwnerContactAcceptanceController.Accept)));

        var policies = RateLimitingServiceCollectionExtensions.Policies.ToDictionary(policy => policy.Name, StringComparer.Ordinal);
        Assert.Equal((10, TimeSpan.FromMinutes(10)), (policies[RateLimitPolicies.OwnerPortalInvite].DefaultPermitLimit, policies[RateLimitPolicies.OwnerPortalInvite].DefaultWindow));
        Assert.Equal((30, TimeSpan.FromMinutes(10)), (policies[RateLimitPolicies.OwnerPortalWrite].DefaultPermitLimit, policies[RateLimitPolicies.OwnerPortalWrite].DefaultWindow));
        Assert.Equal((60, TimeSpan.FromMinutes(10)), (policies[RateLimitPolicies.OwnerPortalDownload].DefaultPermitLimit, policies[RateLimitPolicies.OwnerPortalDownload].DefaultWindow));
        Assert.Equal((120, TimeSpan.FromMinutes(1)), (policies[RateLimitPolicies.OwnerPortalRead].DefaultPermitLimit, policies[RateLimitPolicies.OwnerPortalRead].DefaultWindow));
    }
}
