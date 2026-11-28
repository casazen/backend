using System.Reflection;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

public sealed class DocumentStepLimitsTests
{
    [Fact]
    public void DocumentUpload_TakesTheBudgetOfTheUploadsThatRunTheModels()
    {
        var upload = typeof(PublicGuestCheckInController).GetMethod(nameof(PublicGuestCheckInController.UploadDocument))!;

        var limit = upload.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(limit);
        Assert.Equal(RateLimitPolicies.GuestIdentityUpload, limit.PolicyName);
    }
}
