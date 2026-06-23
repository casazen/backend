using Casazen.Core.Authorization;
using Xunit;

namespace Casazen.Tests.Unit.Auth;

public class ContextAccessBootstrapTests
{
    [Fact]
    public void DeriveContextsFromJwtRoles_MapsKnownRolesToContexts()
    {
        var contexts = ContextAccessBootstrap.DeriveContextsFromJwtRoles([
            "PropertyOwner",
            "LongTermLandlord",
            "Admin"
        ]);

        Assert.Equal(3, contexts.Count);
        Assert.Contains(contexts, c => c.ContextKey == "short-rent" && c.RoleKey == "property_owner");
        Assert.Contains(contexts, c => c.ContextKey == "long-rent" && c.RoleKey == "long_term_landlord");
        Assert.Contains(contexts, c => c.ContextKey == "admin" && c.RoleKey == "platform_admin");
    }

    [Fact]
    public void BuildFallbackAccess_SupplierMapsToSupplierContext()
    {
        var contexts = ContextAccessBootstrap.BuildFallbackAccess(["Supplier"]);
        var supplier = Assert.Single(contexts);
        Assert.Equal("supplier", supplier.ContextKey);
        Assert.Equal("supplier", supplier.RoleKey);
        Assert.Equal("/supplier/inbox", supplier.DefaultRoute);
        Assert.Contains("supplier.inbox.read", supplier.Permissions);
    }

    [Fact]
    public void BuildFallbackAccess_AdminIncludesSeoPermission()
    {
        var contexts = ContextAccessBootstrap.BuildFallbackAccess(["Admin"]);
        var admin = Assert.Single(contexts);
        Assert.Contains("admin.seo.read", admin.Permissions);
    }

    [Fact]
    public void DeriveContextsFromJwtRoles_IgnoresUnknownRoles()
    {
        var contexts = ContextAccessBootstrap.DeriveContextsFromJwtRoles([
            "UnknownRole",
            "PropertyOwner"
        ]);

        Assert.Single(contexts);
        Assert.Equal("short-rent", contexts[0].ContextKey);
    }
}
