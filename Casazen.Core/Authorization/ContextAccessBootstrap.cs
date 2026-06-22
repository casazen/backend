using Casazen.Core.Services;

namespace Casazen.Core.Authorization;

public static class ContextAccessBootstrap
{
    private static readonly BootstrapContext[] BootstrapContexts =
    [
        new(
            JwtRole: "PropertyOwner",
            ContextKey: "short-rent",
            DisplayName: "Affitti brevi",
            RoleKey: "property_owner",
            DefaultRoute: "/app/short-rent",
            Permissions:
            [
                "property.read",
                "property.write",
                "booking.read",
                "booking.write",
                "payment.read",
                "payment.write",
                "ota.read",
                "ota.write",
                "guest.read",
                "guest.write",
            ]),
        new(
            JwtRole: "LongTermLandlord",
            ContextKey: "long-rent",
            DisplayName: "Affitti lungo termine",
            RoleKey: "long_term_landlord",
            DefaultRoute: "/app/long-rent/leases",
            Permissions:
            [
                "lease.read",
                "lease.create",
                "lease.sign",
                "lease.register",
            ]),
        new(
            JwtRole: "Admin",
            ContextKey: "admin",
            DisplayName: "Amministrazione",
            RoleKey: "platform_admin",
            DefaultRoute: "/app/admin",
            Permissions:
            [
                "admin.stats.read",
                "admin.users.read",
                "admin.users.manage",
                "admin.cin.read",
                "admin.jobs.read",
                "admin.seo.read",
                "admin.tax.manage",
            ]),
        new(
            JwtRole: "Supplier",
            ContextKey: "supplier",
            DisplayName: "Fornitore",
            RoleKey: "supplier",
            DefaultRoute: "/supplier/inbox",
            Permissions:
            [
                "supplier.profile.read",
                "supplier.profile.write",
                "supplier.inbox.read",
                "supplier.availability.write",
            ]),
    ];

    public static IReadOnlyList<BootstrapContextMembership> DeriveContextsFromJwtRoles(IEnumerable<string> jwtRoles)
    {
        var rolesSet = jwtRoles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return BootstrapContexts
            .Where(c => rolesSet.Contains(c.JwtRole))
            .Select(c => new BootstrapContextMembership(c.ContextKey, c.RoleKey))
            .ToList();
    }

    public static IReadOnlyList<ContextAccess> BuildFallbackAccess(IEnumerable<string> jwtRoles)
    {
        var rolesSet = jwtRoles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return BootstrapContexts
            .Where(c => rolesSet.Contains(c.JwtRole))
            .Select(c => new ContextAccess(c.ContextKey, c.DisplayName, c.RoleKey, c.Permissions, c.DefaultRoute))
            .ToList();
    }

    private sealed record BootstrapContext(
        string JwtRole,
        string ContextKey,
        string DisplayName,
        string RoleKey,
        string DefaultRoute,
        IReadOnlyList<string> Permissions
    );
}

public sealed record BootstrapContextMembership(string ContextKey, string RoleKey);
