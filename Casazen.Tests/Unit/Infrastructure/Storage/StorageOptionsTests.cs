using Casazen.Core.Services;
using Casazen.Infrastructure.Storage;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure.Storage;

/// <summary>
/// FD-07: outside Development/Testing the storage must be Supabase (S3) and fully configured,
/// otherwise the app does not start (<c>ValidateOnStart</c>).
/// </summary>
public class StorageOptionsTests
{
    private static readonly Dictionary<string, string?> CompleteS3 = new()
    {
        ["Storage:PublicBaseUrl"] = "https://ref.supabase.co/storage/v1/object/public/casazen-prod-public",
        ["Storage:S3:ServiceUrl"] = "https://ref.storage.supabase.co/storage/v1/s3",
        ["Storage:S3:Region"] = "eu-central-1",
        ["Storage:S3:AccessKeyId"] = "key",
        ["Storage:S3:SecretAccessKey"] = "secret",
        ["Storage:S3:PublicBucket"] = "casazen-prod-public",
        ["Storage:S3:PrivateBucket"] = "casazen-prod-private",
    };

    [Fact]
    public void Resolve_ProductionWithoutStorageConfig_FailsListingMissingKeys()
    {
        using var provider = BuildProvider("Production", new Dictionary<string, string?>());

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains(ex.Failures, f => f.Contains("Storage:S3:ServiceUrl"));
        Assert.Contains(ex.Failures, f => f.Contains("Storage:S3:AccessKeyId"));
        Assert.Contains(ex.Failures, f => f.Contains("Storage:S3:PrivateBucket"));
        Assert.Contains(ex.Failures, f => f.Contains("Storage:PublicBaseUrl"));
    }

    [Fact]
    public void Resolve_ProductionWithFileSystemProvider_Fails()
    {
        using var provider = BuildProvider("Production", new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "FileSystem",
            ["App:ApiBaseUrl"] = "https://api.example",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains(ex.Failures, f => f.Contains("only in Development and Testing"));
    }

    [Fact]
    public void Resolve_ProductionWithPlainHttpEndpoint_Fails()
    {
        var config = new Dictionary<string, string?>(CompleteS3) { ["Storage:S3:ServiceUrl"] = "http://ref.storage.supabase.co/storage/v1/s3" };
        using var provider = BuildProvider("Production", config);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains(ex.Failures, f => f.Contains("Storage:S3:ServiceUrl"));
    }

    [Fact]
    public void Resolve_ProductionWithSameBucketForPublicAndPrivate_Fails()
    {
        var config = new Dictionary<string, string?>(CompleteS3) { ["Storage:S3:PrivateBucket"] = "casazen-prod-public" };
        using var provider = BuildProvider("Production", config);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StorageOptions>>().Value);

        Assert.Contains(ex.Failures, f => f.Contains("two different buckets"));
    }

    [Fact]
    public void Resolve_ProductionWithCompleteS3Config_UsesS3Storage()
    {
        using var provider = BuildProvider("Production", CompleteS3);

        Assert.True(provider.GetRequiredService<IOptions<StorageOptions>>().Value.UsesS3);
        Assert.IsType<S3FileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public void Resolve_DevelopmentWithoutConfig_DefaultsToFileSystemWithAbsolutePublicUrl()
    {
        using var provider = BuildProvider("Development", new Dictionary<string, string?>
        {
            ["App:ApiBaseUrl"] = "https://localhost:5001/",
        });

        var options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;

        Assert.True(options.UsesFileSystem);
        Assert.Equal("https://localhost:5001/storage/public", options.PublicBaseUrl);
        Assert.IsType<FileSystemFileStorage>(provider.GetRequiredService<IFileStorage>());
    }

    [Fact]
    public async Task StartAsync_ProductionWithoutStorageConfig_DoesNotStart()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddCasazenFileStorage(builder.Configuration);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    private static ServiceProvider BuildProvider(string environment, IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environment));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddCasazenFileStorage(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Casazen.Tests";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "casazen-storage-options-tests");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
