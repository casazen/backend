using System.Reflection;
using Casazen.Web.Controllers;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US03/T01: a picture of the document runs the geometry of the card and the OCR of its zones in the request, as the frames of a presence sequence run their models: it takes the
/// budget of the uploads that do (<c>GuestIdentityUpload</c>, 6 a minute per client IP and token) and not the 10 a minute of the reads of the form, so the cost one guest can put on the
/// machine in a minute is the one that budget gives.
/// </summary>
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
