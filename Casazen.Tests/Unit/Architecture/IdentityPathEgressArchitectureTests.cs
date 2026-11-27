using System.Reflection;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Architecture;

/// <summary>
/// E70/F02/US02/T02 (NF-197, ADR-013-R21, decision D5): no picture, frame, crop, OCR text or face representation of the identity path can leave the process.
/// The identity path is every type of the namespace <c>Casazen.Infrastructure.Identity</c> and below, found by convention (nested and compiler-generated types
/// included), so a class a later task adds there is covered without touching this test; plus the classes of the flow, the pipeline, the OCR and the readers
/// written before that namespace existed, named in <see cref="NamedPathTypes"/>. Reflection only: no database, no model file, no network and no host (a plain
/// <see cref="ServiceCollection"/> for the registration). Each failure names the type and the member.
/// </summary>
public class IdentityPathEgressArchitectureTests
{
    private const string IdentityNamespace = "Casazen.Infrastructure.Identity";
    private const string ExternalNamespace = "Casazen.Infrastructure.External";

    /// <summary>
    /// The classes of the identity path outside its namespace. <c>BiometricModelStore</c> is left out on purpose: it owns the HTTP client that downloads the
    /// weights of a model (a file of the platform with a pinned SHA-256, never a picture), and no picture reaches it.
    /// </summary>
    private static readonly Type[] NamedPathTypes =
    [
        typeof(IdentityVerificationFlowService), typeof(IdentityVerificationAgent), typeof(CaptureQualityAssessor), typeof(BiometricVerificationService),
        typeof(AntiSpoofingService), typeof(ActivePoseVerificationService), typeof(FaceDetector), typeof(FaceEmbedder), typeof(MrzExtractionService),
        typeof(MrzImageLocator), typeof(TesseractMrzTextReader), typeof(TesseractPageTextReader), typeof(DocumentOcrService), typeof(DocumentReadingService),
    ];

    /// <summary>
    /// The one HTTP use the path already has: the two Tesseract readers fetch their language files (pinned SHA-256, ADR-013-R8) through the client of
    /// <c>BiometricModelStore</c>, and only where a download address is configured (none by default), like the weights of the models. For these two, and for
    /// nothing else, the factory of that client and the client it makes are allowed (<see cref="ModelFileDownloadClient"/>); the picture goes to
    /// <c>ReadLineAsync</c> and <c>ReadPageAsync</c>, which run the library in the process, and never reaches that client. Every other rule holds for them too.
    /// </summary>
    private static readonly Type[] ModelFileDownloaders = [typeof(TesseractMrzTextReader), typeof(TesseractPageTextReader)];

    private static readonly Type[] ModelFileDownloadClient = [typeof(IHttpClientFactory), typeof(Func<HttpClient>)];

    /// <summary>The side channels the flow calls: the alerts to the operators, the lock code, the push and the e-mail queue that the flow and the alerts use.</summary>
    private static readonly Type[] SideChannels =
        [typeof(IVerificationOperatorAlerts), typeof(IAccessCodeService), typeof(IPushNotificationService), typeof(IEmailQueue)];

    /// <summary>
    /// Test A, privacy (AC-NF197-2): no constructor parameter (primary constructors included) and no field, inherited and static ones too, of a type of the path
    /// is an AI provider, the AI settings, an HTTP client or its factory, or any type of <c>Casazen.Infrastructure.External</c>; in the identity namespace not
    /// the Hangfire job client either (ADR-013-R2: no picture goes to a job).
    /// </summary>
    [Fact]
    public void IdentityPathEgress_NoAiOrHttpDependency()
    {
        var offending = IdentityNamespaceTypes().SelectMany(type => ForbiddenDependencies(type, jobsForbidden: true))
            .Concat(NamedPathTypes.SelectMany(WithNestedTypes).SelectMany(type => ForbiddenDependencies(type, jobsForbidden: false)))
            .ToList();

        Assert.True(
            offending.Count == 0,
            $"Nothing of the identity path may hold an AI provider, the AI settings, an HTTP client or a type of {ExternalNamespace}, nor, in {IdentityNamespace}, " +
            "the Hangfire job client: a picture could leave the process (NF-197, ADR-013-R21).\n" + string.Join("\n", offending));
    }

    /// <summary>
    /// Test B (AC-NF197-1): the document reader of the check-in that the registrations of the application build, in the order of Program.cs (the services with
    /// the self-hosted identity verification, then the AI provider), is a type of the identity namespace; the AI registration adds none.
    /// </summary>
    [Fact]
    public void IdentityPathEgress_CheckInDocumentReaderResolvesToTheIdentityNamespace()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.DoesNotContain(new ServiceCollection().AddCasazenAiProvider(configuration), descriptor => descriptor.ServiceType == typeof(ICheckInDocumentReader));

        // The two readers of the document come from AddCasazenServices in the application: here they are doubles, so no model and no library is needed.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped(_ => Mock.Of<IMrzExtractionService>());
        services.AddScoped(_ => Mock.Of<IDocumentOcrService>());
        services.AddSelfHostedIdentityVerification();
        services.AddCasazenAiProvider(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var reader = scope.ServiceProvider.GetRequiredService<ICheckInDocumentReader>();

        Assert.True(
            InNamespace(reader.GetType(), IdentityNamespace),
            $"{nameof(ICheckInDocumentReader)} resolves to {Name(reader.GetType())}, outside {IdentityNamespace}: the check-in must read the document in the process.");
        var elsewhere = services
            .Where(descriptor => descriptor.ServiceType == typeof(ICheckInDocumentReader))
            .Select(descriptor => descriptor.ImplementationType)
            .OfType<Type>()
            .Where(implementation => !InNamespace(implementation, IdentityNamespace))
            .Select(Name)
            .ToList();
        Assert.True(elsewhere.Count == 0, $"{nameof(ICheckInDocumentReader)} is also registered as {string.Join(", ", elsewhere)}, outside {IdentityNamespace}.");
    }

    /// <summary>
    /// Test C, privacy (AC-NF197-3): the side channels the flow legitimately calls take no picture and no vector: no method parameter, nor a public property
    /// or field of one followed down, is bytes, a stream, a float vector, a decoded picture or an upload. So nothing of the guest's face or document can be
    /// handed to an e-mail, a push, an alert or the lock code.
    /// </summary>
    [Fact]
    public void IdentityPathEgress_SideChannelsCarryNoImages()
    {
        // The channels checked here are the ones the flow and its alerts take: if they change, this list changes with them.
        var flow = ConstructorParameterTypes(typeof(IdentityVerificationFlowService));
        var alerts = ConstructorParameterTypes(typeof(VerificationOperatorAlerts));
        Assert.Contains(typeof(IVerificationOperatorAlerts), flow);
        Assert.Contains(typeof(IAccessCodeService), flow);
        Assert.Contains(typeof(IEmailQueue), flow);
        Assert.Contains(typeof(IPushNotificationService), alerts);
        Assert.Contains(typeof(IEmailQueue), alerts);

        var offending = SideChannels
            .SelectMany(channel => new[] { channel }.Concat(channel.GetInterfaces()))
            .SelectMany(channel => channel.GetMethods().Select(method => (Channel: channel, Method: method)))
            .SelectMany(entry => entry.Method.GetParameters().SelectMany(parameter =>
                ImageCarriers(parameter.ParameterType, $"{Name(entry.Channel)}.{entry.Method.Name}({parameter.Name})", [])))
            .ToList();

        Assert.True(
            offending.Count == 0,
            "No picture, frame or vector may be handed to the alerts, the lock code, the push or the e-mail queue (NF-197): no parameter, nor a property of one, " +
            "may be bytes, a stream, a float vector, a decoded picture or an upload.\n" + string.Join("\n", offending));
    }

    /// <summary>
    /// Test D, sanity: the convention cannot silently scan nothing (the identity namespace holds the local reader of the check-in at least), and the guard is not
    /// blind (a class that broke each rule would be named with each member, the allowance of the model files included).
    /// </summary>
    [Fact]
    public void IdentityPathEgress_TheConventionScansTheIdentityNamespace()
    {
        Assert.Contains(typeof(LocalCheckInDocumentReader), IdentityNamespaceTypes());

        var planted = Name(typeof(PlantedEgress));
        var findings = WithNestedTypes(typeof(PlantedEgress)).SelectMany(type => ForbiddenDependencies(type, jobsForbidden: true)).ToList();

        Assert.Contains(findings, finding => finding.StartsWith($"{planted}: constructor parameter 'ai' is an AI provider", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith($"{planted}: constructor parameter 'settings' is the AI settings", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith($"{planted}: constructor parameter 'factory' is an HTTP client factory", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith($"{planted}: constructor parameter 'stripe' is a type of {ExternalNamespace}", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith($"{planted}: constructor parameter 'jobs' is the Hangfire job client", StringComparison.Ordinal));
        Assert.Contains(findings, finding => finding.StartsWith($"{planted}: field '<Client>k__BackingField' is an HTTP client", StringComparison.Ordinal));
        // The job client is a rule of the identity namespace only: the named classes are held to the other rules.
        Assert.DoesNotContain(ForbiddenDependencies(typeof(PlantedEgress), jobsForbidden: false), finding => finding.Contains("'jobs'", StringComparison.Ordinal));
    }

    /// <summary>Every type of the identity namespace and below in the assemblies of the application, nested and compiler-generated types included.</summary>
    private static List<Type> IdentityNamespaceTypes() =>
        new[] { typeof(ICheckInDocumentReader).Assembly, typeof(LocalCheckInDocumentReader).Assembly, typeof(IdentityVerificationServiceCollectionExtensions).Assembly }
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => InNamespace(type, IdentityNamespace))
            .ToList();

    private static IEnumerable<Type> WithNestedTypes(Type type) =>
        new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(WithNestedTypes));

    private static List<Type> ConstructorParameterTypes(Type type) =>
        type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType).ToList();

    /// <summary>The constructor parameters and the fields of <paramref name="type"/> (those of its base types too) that break a rule of the path.</summary>
    private static IEnumerable<string> ForbiddenDependencies(Type type, bool jobsForbidden)
    {
        var downloader = ModelFileDownloaders.Contains(Outermost(type));
        foreach (var constructor in type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                if (Forbidden(parameter.ParameterType, jobsForbidden, downloader) is { } reason)
                    yield return $"{Name(type)}: constructor parameter '{parameter.Name}' is {reason} ({Name(parameter.ParameterType)})";
            }
        }

        for (var declaring = type; declaring is not null && declaring != typeof(object); declaring = declaring.BaseType)
        {
            var fields = declaring.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            foreach (var field in fields)
            {
                if (Forbidden(field.FieldType, jobsForbidden, downloader) is { } reason)
                    yield return $"{Name(type)}: field '{field.Name}' is {reason} ({Name(field.FieldType)})";
            }
        }
    }

    /// <summary>Why a member of this type would let a picture out, or null. A wrapper counts as what it wraps (<c>Lazy&lt;IAiProvider&gt;</c> is an AI provider).</summary>
    private static string? Forbidden(Type type, bool jobsForbidden, bool modelFileDownloader)
    {
        if (modelFileDownloader && ModelFileDownloadClient.Contains(type))
            return null;

        foreach (var part in Parts(type))
        {
            if (typeof(IAiProvider).IsAssignableFrom(part))
                return "an AI provider";
            if (typeof(IAiRuntimeSettings).IsAssignableFrom(part))
                return "the AI settings";
            if (typeof(IHttpClientFactory).IsAssignableFrom(part))
                return "an HTTP client factory";
            if (typeof(HttpMessageInvoker).IsAssignableFrom(part) || typeof(HttpMessageHandler).IsAssignableFrom(part))
                return "an HTTP client";
            if (InNamespace(part, ExternalNamespace))
                return $"a type of {ExternalNamespace}";
            if (jobsForbidden && typeof(IBackgroundJobClient).IsAssignableFrom(part))
                return "the Hangfire job client";
        }

        return null;
    }

    /// <summary>
    /// Where a value of <paramref name="type"/> can carry a picture or a vector: the type and what it is made of and, for a type of the application, its public
    /// properties and fields, followed down. Each finding names the path to it.
    /// </summary>
    private static IEnumerable<string> ImageCarriers(Type type, string path, HashSet<Type> seen)
    {
        if (!seen.Add(type))
            yield break;

        if (Parts(type).FirstOrDefault(IsImageOrVector) is { } carrier)
        {
            yield return $"{path} can carry {Name(carrier)}";
            yield break;
        }

        foreach (var part in Parts(type).Where(part => !part.IsEnum && InNamespace(part, "Casazen")))
        {
            foreach (var property in part.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                foreach (var finding in ImageCarriers(property.PropertyType, $"{path}.{property.Name}", seen))
                    yield return finding;
            }

            foreach (var field in part.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                foreach (var finding in ImageCarriers(field.FieldType, $"{path}.{field.Name}", seen))
                    yield return finding;
            }
        }
    }

    /// <summary>Bytes, a float vector, a stream, a decoded picture or an upload, in any of their usual shapes.</summary>
    private static bool IsImageOrVector(Type type) =>
        typeof(IEnumerable<byte>).IsAssignableFrom(type) || typeof(IEnumerable<float>).IsAssignableFrom(type)
        || type == typeof(Memory<byte>) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(Span<byte>) || type == typeof(ReadOnlySpan<byte>)
        || type == typeof(Memory<float>) || type == typeof(ReadOnlyMemory<float>)
        || typeof(Stream).IsAssignableFrom(type) || typeof(RasterImage).IsAssignableFrom(type)
        || typeof(IFormFile).IsAssignableFrom(type) || typeof(IEnumerable<IFormFile>).IsAssignableFrom(type);

    /// <summary>The type and what it is made of: the element of an array or of a reference, and the arguments of a generic type, followed down.</summary>
    private static IEnumerable<Type> Parts(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var part in Parts(type.GetElementType()!))
                yield return part;
        }

        if (type.IsGenericType)
        {
            foreach (var part in type.GetGenericArguments().SelectMany(Parts))
                yield return part;
        }
    }

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is { } outer)
            type = outer;
        return type;
    }

    private static bool InNamespace(Type type, string root) =>
        type.Namespace is { } name && (name == root || name.StartsWith(root + ".", StringComparison.Ordinal));

    /// <summary>A readable name: namespace and type, with the generic arguments.</summary>
    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
            return type.Name;
        if (type.HasElementType)
            return Name(type.GetElementType()!) + (type.IsArray ? "[]" : type.IsByRef ? "&" : "*");

        var name = (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name;
        if (!type.IsGenericType)
            return name;

        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return $"{(tick < 0 ? name : name[..tick])}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }

    /// <summary>A class that breaks every rule of the path, kept outside the identity namespace (it would fail the guard there): the guard must name each member.</summary>
    private sealed class PlantedEgress(
        IAiProvider ai,
        Lazy<IAiRuntimeSettings> settings,
        IHttpClientFactory factory,
        StripeWebhookHandler stripe,
        IBackgroundJobClient jobs)
    {
        public HttpClient? Client { get; set; }

        public override string ToString() => $"{ai}{settings}{factory}{stripe}{jobs}";
    }
}
