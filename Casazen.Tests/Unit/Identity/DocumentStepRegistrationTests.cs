using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E71/F01/US03/T01: the document step of the self-hosted flow runs its checks only when the flow holds the five services of the step (the decoder and geometry of a picture, the classifier
/// of the sides, the checker of the coherence, the registry of the formats and the policy). A flow built by hand without them reads the document as it always did, so what makes the
/// application hold them is the registration: this proves it, with the registrations of the application (the part <c>AddSelfHostedIdentityVerification</c>) and doubles only for what the
/// application registers elsewhere (the models, the readers, the storage). A service that stopped being registered would turn the checks off in silence: it fails here.
/// </summary>
public sealed class DocumentStepRegistrationTests
{
    [Fact]
    public async Task Registration_TheFlowIsBuiltWithTheFiveServicesOfTheDocumentStep()
    {
        await using var provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"document-step-registration-{Guid.NewGuid()}").Options, null, new EphemeralDataProtectionProvider());

        var flow = ActivatorUtilities.CreateInstance<IdentityVerificationFlowService>(
            scope.ServiceProvider,
            db,
            Mock.Of<IIdentityVerificationAgent>(),
            Mock.Of<IVerificationOperatorAlerts>(),
            Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(),
            Mock.Of<IMrzExtractionService>(),
            Mock.Of<IDocumentOcrService>(),
            Mock.Of<IStayGuestService>(),
            Mock.Of<IFileStorage>(),
            new EphemeralDataProtectionProvider(),
            TimeProvider.System,
            NullLogger<IdentityVerificationFlowService>.Instance);

        Assert.True(flow.DocumentChecksWired);
    }

    [Fact]
    public async Task Registration_TheServicesOfTheStepHaveTheLifetimeTheyNeed_AndAreBuilt()
    {
        var services = Services();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        // The decoder holds the reader of the zones, which is scoped (it reads the MRZ through the reader of the request): one for each request. The others keep no picture: one for all.
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(DocumentPictureAnalyzer)).Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ZonedPrefillServices)).Lifetime);
        foreach (var singleton in new[] { typeof(DocumentSideClassifier), typeof(DocumentCoherenceChecker), typeof(DocumentFormatRegistry), typeof(IdentityPolicyProvider) })
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, descriptor => descriptor.ServiceType == singleton).Lifetime);

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DocumentPictureAnalyzer>());
        var zoned = scope.ServiceProvider.GetRequiredService<ZonedPrefillServices>();
        Assert.NotNull(zoned.Pictures);
        Assert.NotNull(zoned.Formats);
        Assert.NotNull(zoned.Sides);
    }

    /// <summary>The registrations of the identity step of the application, with doubles for the models, the readers and the options it registers elsewhere.</summary>
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<BiometricModelOptions>>(Options.Create(new BiometricModelOptions()));
        services.AddSingleton(Mock.Of<IFaceDetector>());
        services.AddSingleton(Mock.Of<IPageTextReader>());
        services.AddScoped(_ => Mock.Of<IMrzExtractionService>());
        services.AddScoped(_ => Mock.Of<IDocumentOcrService>());
        services.AddSelfHostedIdentityVerification();
        return services;
    }
}
