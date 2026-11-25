using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

public sealed class OctorateMasterBoundary(AppDbContext db, IOptions<OctorateOptions> options, IHostEnvironment environment)
{
    private readonly OctorateEnvironment _environment = OctorateEnvironments.Of(environment);

    public Task<bool> IsManagedAsync(Guid orgId, CancellationToken ct) =>
        db.OctorateConnections.AsNoTracking().AnyAsync(c => c.OrgId == orgId && c.Environment == _environment && c.UsesMasterAccount, ct);

    public async Task<string[]?> AssignedAsync(Guid orgId, CancellationToken ct)
    {
        var connectionId = await SharedConnectionIdAsync(orgId, ct);
        return connectionId is { } id
            ? await Assignments(orgId, id).Distinct().ToArrayAsync(ct)
            : null;
    }

    public async Task<Guid> AuthorizeAsync(Guid orgId, HttpMethod method, Uri target, CancellationToken ct)
    {
        var connectionId = await SharedConnectionIdAsync(orgId, ct);
        if (connectionId is null)
            return orgId;
        if (options.Value.MasterOrganizationId is not { } masterId || masterId == orgId)
            throw OctorateAuthException.NotConnected();

        var segments = target.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rest = Array.IndexOf(segments, "rest");
        if (rest >= 0 && segments.Skip(rest).ToArray() is ["rest", "v1", "api", "configuration"] && method == HttpMethod.Get)
            return masterId;

        var accommodationId = ReadAccommodation(target, method, segments, rest);
        if (accommodationId is null || !await Assignments(orgId, connectionId.Value).ContainsAsync(accommodationId, ct))
            throw OctorateAuthException.NotConnected();
        return masterId;
    }

    private Task<Guid?> SharedConnectionIdAsync(Guid orgId, CancellationToken ct) =>
        options.Value.MasterOrganizationId is null ? Task.FromResult<Guid?>(null) :
        db.OctorateConnections.AsNoTracking().Where(c => c.OrgId == orgId && c.Environment == _environment && c.UsesMasterAccount)
            .Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);

    private IQueryable<string> Assignments(Guid orgId, Guid connectionId) =>
        from mapping in db.OctorateListingMappings.AsNoTracking()
        join property in db.Properties.AsNoTracking() on mapping.PropertyId equals (Guid?)property.Id
        where mapping.OrgId == orgId && property.OrgId == orgId && mapping.ConnectionId == connectionId
            && mapping.Status == OctorateMappingStatus.Confirmed
        select mapping.OctorateAccommodationId;

    private static string? ReadAccommodation(Uri target, HttpMethod method, string[] segments, int rest)
    {
        if (rest < 0 || segments.Length <= rest + 2)
            return null;
        var route = segments[(rest + 1)..];
        if (route is ["v1", "connection"] && method == HttpMethod.Get)
        {
            var query = QueryHelpers.ParseQuery(target.Query);
            return query.TryGetValue("accommodation", out var ids) && ids.Count == 1 ? ids[0] : null;
        }
        if (route is ["v1", "connection", var action, var accommodation, _, ..]
            && (action is "external" or "mapping" || action.All(c => char.IsAsciiLetterUpper(c) || c == '_')))
            return accommodation;
        if (route is ["v1", "content", "products", var productAccommodation, _, ..])
            return productAccommodation;
        if (route is ["v1", "accommodation" or "calendar" or "chat" or "checkin" or "connection" or "guests" or "reservation", var v1, ..])
            return v1;
        if (route is ["v2", "accommodation" or "checkin" or "invoice" or "reservation" or "review", var v2, ..])
            return v2;
        if (route is ["v3", "roomrates", var v3, ..])
            return v3;
        return null;
    }
}
