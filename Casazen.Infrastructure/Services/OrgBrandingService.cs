using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Public-site branding of an org (BK-12, A3-17). Images go to the public bucket of <see cref="IFileStorage"/> (FD-07)
/// under <c>orgs/{orgId}/branding/{kind}/</c> and are referenced by their absolute public URL.
/// </summary>
public class OrgBrandingService(
    AppDbContext dbContext,
    IFileStorage storage,
    ILogger<OrgBrandingService> logger) : IOrgBrandingService
{
    public async Task<Org?> UpdateAsync(Guid orgId, OrgBrandingUpdate update, CancellationToken cancellationToken = default)
    {
        // Validate everything before loading anything: an invalid value changes nothing.
        var primaryColor = OrgBrandingRules.NormalizePrimaryColor(update.PrimaryColor);
        var themeId = OrgBrandingRules.NormalizeThemeId(update.PublicThemeId);
        var tagline = OrgBrandingRules.NormalizeTagline(update.Tagline);

        // DB-03: the public profile fields are validated only when the caller sends them; the others keep their value.
        var subtitle = update.Subtitle.IsSent ? OrgBrandingRules.NormalizeSubtitle(update.Subtitle.Value) : null;
        var hostName = update.HostName.IsSent ? OrgBrandingRules.NormalizeHostName(update.HostName.Value) : null;
        var publicPhone = update.PublicPhone.IsSent ? OrgBrandingRules.NormalizePublicPhone(update.PublicPhone.Value) : null;

        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        org.ThemeColor = primaryColor;
        org.PublicThemeId = themeId;
        org.Tagline = tagline;
        if (update.Subtitle.IsSent)
            org.Subtitle = subtitle;
        if (update.HostName.IsSent)
            org.HostName = hostName;
        if (update.PublicPhone.IsSent)
            org.PublicPhone = publicPhone;
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return org;
    }

    public async Task<Org?> SetImageAsync(
        Guid orgId,
        BrandingImageKind kind,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var spec = OrgBrandingRules.SpecFor(kind);
        var bytes = await ReadAtMostAsync(content, spec.MaxBytes + 1, cancellationToken);
        var image = OrgBrandingRules.ValidateImage(spec, bytes);

        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        var key = StorageKeys.OrgBrandingImage(orgId, kind, $"{Guid.NewGuid():N}{image.Extension}");
        using (var stream = new MemoryStream(bytes, writable: false))
            await storage.PutAsync(StorageBucket.Public, key, stream, image.ContentType, cancellationToken);

        var previousUrl = GetUrl(org, kind);
        SetUrl(org, kind, storage.GetPublicUrl(key));
        org.UpdatedAt = DateTime.UtcNow;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // The org still points at the previous image: the new object would be orphaned.
            await TryDeleteAsync(orgId, key, CancellationToken.None);
            throw;
        }

        logger.LogInformation(
            "Branding {Kind} of org {OrgId} replaced ({Format} {Width}x{Height}, {Bytes} bytes)",
            kind, orgId, image.Format, image.Width, image.Height, bytes.Length);
        await DeleteOwnImageAsync(orgId, kind, previousUrl, cancellationToken);
        return org;
    }

    public async Task<Org?> RemoveImageAsync(Guid orgId, BrandingImageKind kind, CancellationToken cancellationToken = default)
    {
        var org = await dbContext.Orgs.FirstOrDefaultAsync(o => o.Id == orgId, cancellationToken);
        if (org is null)
            return null;

        var previousUrl = GetUrl(org, kind);
        if (previousUrl is null)
            return org;

        SetUrl(org, kind, null);
        org.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Branding {Kind} of org {OrgId} removed", kind, orgId);
        await DeleteOwnImageAsync(orgId, kind, previousUrl, cancellationToken);
        return org;
    }

    private static string? GetUrl(Org org, BrandingImageKind kind) =>
        kind == BrandingImageKind.Logo ? org.LogoUrl : org.HeroImageUrl;

    private static void SetUrl(Org org, BrandingImageKind kind, string? url)
    {
        if (kind == BrandingImageKind.Logo)
            org.LogoUrl = url;
        else
            org.HeroImageUrl = url;
    }

    /// <summary>
    /// Deletes a replaced or removed image only when it is a branding object of this org in the storage: a URL set by
    /// other means (external, or another object of the bucket) is left alone. Best effort, after the database change:
    /// a failure leaves an orphan object, never a broken site.
    /// </summary>
    private async Task DeleteOwnImageAsync(Guid orgId, BrandingImageKind kind, string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        var key = storage.TryGetPublicKey(url);
        var ownPrefix = StorageKeys.OrgBrandingImage(orgId, kind, string.Empty);
        if (key is null || !key.StartsWith(ownPrefix, StringComparison.Ordinal))
            return;

        await TryDeleteAsync(orgId, key, cancellationToken);
    }

    private async Task TryDeleteAsync(Guid orgId, string key, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(StorageBucket.Public, key, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not delete branding object {Key} of org {OrgId}", key, orgId);
        }
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes: enough to tell a file over the size limit (one byte more) without
    /// buffering an arbitrarily large upload.
    /// </summary>
    private static async Task<byte[]> ReadAtMostAsync(Stream content, long limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length < limit)
        {
            var toRead = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = await content.ReadAsync(chunk.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
