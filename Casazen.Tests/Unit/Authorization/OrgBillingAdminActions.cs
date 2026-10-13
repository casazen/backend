using System.Reflection;
using System.Text.RegularExpressions;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Casazen.Tests.Unit.Authorization;

/// <summary>
/// One routed action of the API protected by the org policy <see cref="CasazenPolicies.OrgBillingAdmin"/> (AM-00): the
/// HTTP verb, the route template with the controller's prefix, the body type it consumes and whether an
/// <c>[AllowAnonymous]</c> overrides the policy.
/// </summary>
internal sealed partial record OrgBillingAdminAction(
    Type Controller,
    MethodInfo Method,
    string HttpMethod,
    string RouteTemplate,
    string? ConsumedContentType,
    bool IsAnonymous,
    IReadOnlyList<string> Policies)
{
    public string Key => $"{Controller.Name}.{Method.Name}";

    /// <summary>The route with every parameter replaced by a value of its constraint type (<c>orgId</c> by the given org).</summary>
    public string ResolveRoute(Guid orgId) =>
        "/" + RouteParameter().Replace(RouteTemplate, match =>
        {
            if (match.Groups["name"].Value.Equals("orgId", StringComparison.OrdinalIgnoreCase))
                return orgId.ToString();

            return match.Groups["constraint"].Value switch
            {
                "guid" => Guid.NewGuid().ToString(),
                "int" or "long" => "1",
                _ => "x",
            };
        });

    public override string ToString() => $"{HttpMethod} /{RouteTemplate} ({Key})";

    [GeneratedRegex(@"\{(?<name>[^}:?]+)(?::(?<constraint>[^}?]+))?\??\}")]
    private static partial Regex RouteParameter();
}

/// <summary>
/// Every action of the API whose class or method carries <c>[Authorize(Policy = OrgBillingAdmin)]</c>, found by
/// reflection like <see cref="EndpointAuthorizationArchitectureTests"/> does: a new endpoint under the policy is picked up
/// by the tests on the policy without anyone listing it (AM-00).
/// </summary>
internal static class OrgBillingAdminActions
{
    public static IReadOnlyList<OrgBillingAdminAction> All { get; } = Discover();

    private static List<OrgBillingAdminAction> Discover()
    {
        var found = new List<OrgBillingAdminAction>();
        var controllers = typeof(PaymentsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t is { IsAbstract: false, IsPublic: true })
            .OrderBy(t => t.Name, StringComparer.Ordinal);

        foreach (var controller in controllers)
        {
            var classAttributes = controller.GetCustomAttributes(inherit: true);
            var classAnonymous = classAttributes.OfType<IAllowAnonymous>().Any();
            var classAuthorize = classAttributes.OfType<IAuthorizeData>().ToList();
            var classRoute = classAttributes.OfType<RouteAttribute>().FirstOrDefault()?.Template ?? string.Empty;
            classRoute = classRoute.Replace("[controller]", controller.Name.Replace("Controller", string.Empty), StringComparison.Ordinal);

            var actions = controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null)
                .OrderBy(m => m.Name, StringComparer.Ordinal);

            foreach (var action in actions)
            {
                var attributes = action.GetCustomAttributes(inherit: true);
                var verb = attributes.OfType<HttpMethodAttribute>().FirstOrDefault();
                if (verb is null)
                    continue;

                var policies = classAuthorize
                    .Concat(attributes.OfType<IAuthorizeData>())
                    .Select(a => a.Policy)
                    .OfType<string>()
                    .ToList();
                if (!policies.Contains(CasazenPolicies.OrgBillingAdmin, StringComparer.Ordinal))
                    continue;

                var template = (verb.Template ?? string.Empty).TrimStart('/');
                var route = string.IsNullOrEmpty(template) ? classRoute : $"{classRoute}/{template}".Trim('/');
                found.Add(new OrgBillingAdminAction(
                    controller,
                    action,
                    verb.HttpMethods.First(),
                    route,
                    attributes.OfType<ConsumesAttribute>().FirstOrDefault()?.ContentTypes.FirstOrDefault(),
                    classAnonymous || attributes.OfType<IAllowAnonymous>().Any(),
                    policies));
            }
        }

        return found;
    }
}
