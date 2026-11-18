using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Middleware;

/// <summary>New commercial signups cannot use host operations before the subscription webhook confirms payment.</summary>
public sealed class CommercialAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRequestTenantContext tenant, AppDbContext db)
    {
        var policies = context.GetEndpoint()?.Metadata.GetOrderedMetadata<IAuthorizeData>();
        // A dual-role account must keep its independent supplier workflow.
        if (policies?.Any(a => a.Policy == "RequireSupplier") == true)
        {
            await next(context);
            return;
        }
        var hostOperation = policies?.Any(a => a.Policy?.StartsWith("RequireContext:short-rent", StringComparison.Ordinal) == true ||
                      a.Policy?.StartsWith("RequireContext:long-rent", StringComparison.Ordinal) == true) == true;
        var controller = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerName;
        var module = ModuleForController(controller);
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>()?.ActionName;
        // Passive dashboard summaries remain part of the base experience.
        if (controller == "Compliance" && action == "GetSummary") module = null;
        if (controller == "ServiceRequests" && (action == "GetById" ||
            string.Equals(context.Request.Query["view"], "supplier", StringComparison.OrdinalIgnoreCase))) module = null;
        hostOperation |= controller is "OrgInvitations" or "OrgMembers";
        if ((hostOperation || module is not null) && context.User.Identity?.IsAuthenticated == true && tenant.OrgId is Guid orgId)
        {
            var org = await db.Orgs.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId, context.RequestAborted);
            if (org?.CommercialSignupJson is not null &&
                (!org.CommercialPaymentConfirmed || org.SubscriptionStatus is not (SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue)))
            {
                var problem = ApiProblemDetails.Create(context, 403, "onboarding_required", "OnboardingConsentsRequired");
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(problem, cancellationToken: context.RequestAborted);
                return;
            }
            if (org?.CommercialSignupJson is not null && module is not null)
            {
                var paid = System.Text.Json.JsonSerializer.Deserialize<string[]>(org.CommercialPaidAddonsJson ?? "[]") ?? [];
                if (!paid.Contains(module, StringComparer.Ordinal))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { code = "commercial_module_required", module }, context.RequestAborted);
                    return;
                }
            }
        }
        await next(context);
    }
    public static string? ModuleForController(string? controller) => controller switch
    {
        "Ota" or "OtaIntegrations" or "Octorate" or "OctorateMapping" or "OctorateExistingInventory" or "BookingOctorateSync" => "channels",
        "Routines" => "ai-agents",
        "Verification" => "identification",
        "Compliance" or "ComplianceAutopilot" or "ComplianceArchive" or "ComplianceConnectionHealth" or "OrgComplianceSender" or "PropertyIstatCredentials" or "Alloggiati" or "Istat" or "OctorateComplianceHandoff" => "compliance",
        "Connect" or "OrgDomain" or "OrgSiteDocuments" or "OrgBranding" => "direct-booking",
        "AssistantRules" or "AssistantDrafts" or "AssistantKnowledge" or "AssistantSituations" => "messages-ai",
        "ServiceRequests" or "ServiceRequestSuppliers" or "ServiceRequestAssist" or "LongRentServiceRequests" => "operations",
        "FiscalDocuments" or "FiscalDocumentSettings" or "FiscalDocumentSdi" or "FiscalDocumentExports" or "OctorateDocuments" or "LongRentFiscal" => "fiscal",
        "PricingAdapter" => "revenue",
        "Crm" or "CrmContacts" => "crm",
        _ => null,
    };
}
