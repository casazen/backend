using Amazon.S3;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// Object storage wiring (FD-07, decision D8). Supabase Storage (S3 API) everywhere except
/// Development/Testing, where a local-disk provider is allowed. Runbook: docs/runbooks/storage.md.
/// </summary>
public static class StorageExtensions
{
    public static IServiceCollection AddCasazenFileStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<StorageOptions>, StorageOptionsDefaults>();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();

        // Resolved only when the S3 provider is in use.
        services.AddSingleton<IAmazonS3>(sp =>
            S3FileStorage.CreateClient(sp.GetRequiredService<IOptions<StorageOptions>>().Value));
        services.AddSingleton<IFileStorage>(sp =>
            sp.GetRequiredService<IOptions<StorageOptions>>().Value.UsesFileSystem
                ? ActivatorUtilities.CreateInstance<FileSystemFileStorage>(sp)
                : ActivatorUtilities.CreateInstance<S3FileStorage>(sp));

        services.AddScoped<IImageStorageService, ImageStorageService>();
        services.AddScoped<IGuestDocumentStorage, GuestDocumentStorageService>();
        services.AddScoped<LegacyFileMigrationService>();
        return services;
    }

    /// <summary>True when the process was started as the legacy file migration command.</summary>
    public static bool IsLegacyFileMigrationCommand(string[] args) =>
        args.Contains(LegacyFileMigrationService.CommandName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs <c>storage:migrate-legacy [--dry-run]</c>: copies the files of the old local-disk storage
    /// into the configured object storage and rewrites the database references. Returns the exit code.
    /// </summary>
    public static async Task<int> RunLegacyFileMigrationAsync(this WebApplication app, string[] args)
    {
        var dryRun = args.Contains(LegacyFileMigrationService.DryRunFlag, StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var migration = scope.ServiceProvider.GetRequiredService<LegacyFileMigrationService>();
            var report = await migration.MigrateAsync(dryRun);
            Console.WriteLine(
                $"{LegacyFileMigrationService.CommandName}{(dryRun ? " (dry run)" : string.Empty)}: " +
                $"uploaded={report.Uploaded} alreadyPresent={report.AlreadyPresent} missing={report.Missing} " +
                $"referencesRewritten={report.ReferencesRewritten}");
            return 0;
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Legacy file migration failed");
            return 1;
        }
    }

    /// <summary>
    /// Static files: <c>wwwroot</c> (test iCal feeds) but never the legacy <c>/uploads</c> folder, which
    /// used to expose property documents anonymously (A2-31). With the filesystem provider
    /// (Development/Testing only) the public folder is served at <see cref="FileSystemFileStorage.PublicRequestPath"/>
    /// and a warning is logged; the private folder is never served.
    /// </summary>
    public static WebApplication UseCasazenStaticFiles(this WebApplication app)
    {
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/uploads"),
            branch => branch.UseStaticFiles());

        var options = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
        if (!options.UsesFileSystem)
            return app;

        var storage = (FileSystemFileStorage)app.Services.GetRequiredService<IFileStorage>();
        var publicRoot = storage.BucketRoot(StorageBucket.Public);
        Directory.CreateDirectory(publicRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(publicRoot),
            RequestPath = FileSystemFileStorage.PublicRequestPath,
        });

        app.Logger.LogWarning(
            "STORAGE: local filesystem provider in use ({Environment} only). Files are written to {RootPath} and are NOT durable; " +
            "deployed environments must use Supabase Storage (Storage:Provider=S3, see docs/runbooks/storage.md).",
            app.Environment.EnvironmentName,
            storage.RootPath);
        return app;
    }
}
