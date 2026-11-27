using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

public static class CheckInIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenCheckInIdentityServices(this IServiceCollection services)
    {
        services.AddOptions<Casazen.Core.Options.MrzOcrOptions>()
            .BindConfiguration(Casazen.Core.Options.MrzOcrOptions.SectionName);
        services.AddSingleton<IMrzBandLocator, MrzImageLocator>();
        services.AddSingleton<TesseractMrzTextReader>();
        services.AddSingleton<IMrzTextReader>(serviceProvider => serviceProvider.GetRequiredService<TesseractMrzTextReader>());
        services.AddSingleton<TesseractPageTextReader>();
        services.AddSingleton<IPageTextReader>(serviceProvider => serviceProvider.GetRequiredService<TesseractPageTextReader>());
        services.AddScoped<IMrzExtractionService, MrzExtractionService>();
        services.AddScoped<IDocumentOcrService, DocumentOcrService>();
        services.AddScoped<IDocumentReadingService, DocumentReadingService>();
        services.AddScoped<ICheckInWizardService, CheckInWizardService>();
        services.AddOptions<Casazen.Core.Options.BiometricModelOptions>()
            .BindConfiguration(Casazen.Core.Options.BiometricModelOptions.SectionName);
        services.AddHttpClient(BiometricModelStore.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<BiometricModelStore>();
        services.AddSingleton<IBiometricModelStore>(serviceProvider => serviceProvider.GetRequiredService<BiometricModelStore>());
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BiometricModelStore>());
        services.AddSingleton<IBiometricModelStatusReader, BiometricModelStatusReader>();
        services.AddSingleton<IFaceDetector, FaceDetector>();
        services.AddSingleton<IFaceEmbedder, FaceEmbedder>();
        services.AddScoped<IAntiSpoofingService, AntiSpoofingService>();
        services.AddScoped<IBiometricVerificationService, BiometricVerificationService>();
        services.AddScoped<IActivePoseVerificationService, ActivePoseVerificationService>();
        services.AddSingleton<CaptureQualityAssessor>();
        services.AddScoped<IIdentityVerificationAgent, IdentityVerificationAgent>();
        services.AddScoped<IIdentityVerificationFlowService, IdentityVerificationFlowService>();
        // E70/F01/US01/T01 (NF-188..NF-199): the self-hosted identity verification, its options (section IdentityVerification) and the hooks of the tasks of
        // E70-E74, which register their services only in their own part of IdentityVerificationServiceCollectionExtensions, never here. Read at run time
        // behind Features:CheckInIdentitySelfHosted.
        services.AddSelfHostedIdentityVerification();
        services.AddScoped<IVerificationQueueService, VerificationQueueService>();
        services.AddScoped<IBiometricSettingsService, BiometricSettingsService>();
        services.AddScoped<IBookingFlowSettingsService, BookingFlowSettingsService>();
        services.AddScoped<IVerificationOperatorAlerts, VerificationOperatorAlerts>();
        services.AddScoped<IAccessCodeService, AccessCodeService>();

        return services;
    }
}
