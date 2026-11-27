using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The identity side of the guest check-in: reading of the document, biometric verification, the identity verification agent and flow, the operator
/// queue, the biometric and booking flow settings, and the access codes.
/// Called by <see cref="ServiceCollectionExtensions.AddCasazenServices"/> in the position its registrations had when they were all in one method: the order of the
/// registrations matters for the services resolved as <c>IEnumerable&lt;T&gt;</c> and for the hosted services.
/// </summary>
public static class CheckInIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenCheckInIdentityServices(this IServiceCollection services)
    {
        // NF-10 / NF-24: reading of the identity document of the guest check-in, in memory only (ADR-009-R22, ADR-013-R6, R8, R10,
        // E03/F04/US01/T02). The MRZ locator is stateless; the Tesseract readers bind the library installed in the image the first time a
        // document is read and verify the SHA-256 of their language files (section MrzOcr, docs/TECHNICAL.md).
        services.AddOptions<Casazen.Core.Options.MrzOcrOptions>()
            .BindConfiguration(Casazen.Core.Options.MrzOcrOptions.SectionName);
        services.AddSingleton<IMrzBandLocator, MrzImageLocator>();
        services.AddSingleton<TesseractMrzTextReader>();
        services.AddSingleton<IMrzTextReader>(serviceProvider => serviceProvider.GetRequiredService<TesseractMrzTextReader>());
        services.AddSingleton<TesseractPageTextReader>();
        services.AddSingleton<IPageTextReader>(serviceProvider => serviceProvider.GetRequiredService<TesseractPageTextReader>());
        services.AddScoped<IMrzExtractionService, MrzExtractionService>();
        services.AddScoped<IDocumentOcrService, DocumentOcrService>();
        // E03/F01/US01/T04: the shared reading of a document photo (MRZ, then OCR, in memory only) and the host wizard "Guest check-in", which
        // composes the guests, the tax, the sender and the check-in on Octorate (NF-40, NF-45).
        services.AddScoped<IDocumentReadingService, DocumentReadingService>();
        services.AddScoped<ICheckInWizardService, CheckInWizardService>();
        // NF-11 / NF-24: Biometric verification for guest check-in (ADR-013, E03/F04/US01/T01). The three ONNX models are verified (SHA-256)
        // and loaded once, in the background, where Features:CheckInIdentityVerification is on; without them every verification is the operator
        // path (section Biometrics, docs/TECHNICAL.md). The large recognition model is downloaded with the client below.
        services.AddOptions<Casazen.Core.Options.BiometricModelOptions>()
            .BindConfiguration(Casazen.Core.Options.BiometricModelOptions.SectionName);
        services.AddHttpClient(BiometricModelStore.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<BiometricModelStore>();
        services.AddSingleton<IBiometricModelStore>(serviceProvider => serviceProvider.GetRequiredService<BiometricModelStore>());
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<BiometricModelStore>());
        // E22/F01/US01/T01: what the models are doing, read by the non-blocking health entry "biometrics" and by GET api/admin/biometrics/status.
        services.AddSingleton<IBiometricModelStatusReader, BiometricModelStatusReader>();
        services.AddSingleton<IFaceDetector, FaceDetector>();
        services.AddSingleton<IFaceEmbedder, FaceEmbedder>();
        services.AddScoped<IAntiSpoofingService, AntiSpoofingService>();
        services.AddScoped<IBiometricVerificationService, BiometricVerificationService>();
        services.AddScoped<IActivePoseVerificationService, ActivePoseVerificationService>();
        // E03/F04/US02/T02: the identity verification agent on top of that pipeline: the capture checks and one rule-based decision with
        // versioned thresholds. No AI provider, no logger, no storage among its dependencies (PO-09).
        services.AddSingleton<CaptureQualityAssessor>();
        services.AddScoped<IIdentityVerificationAgent, IdentityVerificationAgent>();
        // E03/F04/US02/T04: the verification of one check-in session (consent, document, selfie, operator, lock code release), behind
        // Features:CheckInIdentityVerification like the public endpoints that call it.
        services.AddScoped<IIdentityVerificationFlowService, IdentityVerificationFlowService>();
        // E70/F01/US01/T01 (NF-188..NF-199): the self-hosted identity verification, its options (section IdentityVerification) and the hooks of the tasks of
        // E70-E74, which register their services only in their own part of IdentityVerificationServiceCollectionExtensions, never here. Read at run time
        // behind Features:CheckInIdentitySelfHosted.
        services.AddSelfHostedIdentityVerification();
        // NF-12: Operator verification queue for manual biometric decisions (ADR-013).
        services.AddScoped<IVerificationQueueService, VerificationQueueService>();
        // E22/F01/US01/T03: the biometric settings of an organization (the thresholds and the attempts, provisional until a platform administrator marks a
        // calibration), read by its administrators and the staff; the flow reads the same row.
        services.AddScoped<IBiometricSettingsService, BiometricSettingsService>();
        // casazen/backend#978: which steps of a stay CasaZen does by itself (the check-in link, the arrival, the departure, the property ready), per org.
        services.AddScoped<IBookingFlowSettingsService, BookingFlowSettingsService>();
        // E03/F04/US02/T03: the immediate alert to the operators (push, e-mail, urgent "To do") when a verification needs a person.
        services.AddScoped<IVerificationOperatorAlerts, VerificationOperatorAlerts>();
        // NF-13: Property access codes with encryption and verification gating (E06/F03/US01/T01).
        services.AddScoped<IAccessCodeService, AccessCodeService>();

        return services;
    }
}
