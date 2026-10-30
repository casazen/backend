using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// What the tests of the request on measure from a supplier's public showcase (SP-10b) add to <see cref="ServiceRequestScenario"/>: a request is
/// made the way the API makes it (written, then the address checked), and what the customer wrote is known.
/// </summary>
internal static class InquiryScenario
{
    public const string Words = "Vorrei far sistemare la ringhiera del balcone e controllare la porta d'ingresso che si blocca.";

    /// <summary>What the customer sends: a valid request about no service in particular, in the supplier's comune.</summary>
    public static ShowcaseInquiryInput InquiryInput(string? email = ShowcaseScenario.CustomerEmail) =>
        new(
            Guid.NewGuid(),
            null,
            null,
            ShowcaseScenario.City,
            Words,
            ShowcaseScenario.CustomerName,
            email,
            ShowcaseScenario.CustomerPhone,
            "it",
            true,
            ServiceRequestTestKit.PrivacyNoticeVersion,
            ShowcaseScenario.ConsentIp);

    /// <summary>
    /// A request on measure made through the service and confirmed: the request as saved. The notifications of the making are forgotten, so a
    /// test sees only what its own action queues.
    /// </summary>
    public static async Task<ServiceRequest> InquiredAsync(
        this ServiceRequestScenario s,
        SupplierProfile? supplier = null,
        string? email = ShowcaseScenario.CustomerEmail)
    {
        supplier ??= await s.SupplierAsync();
        s.ForgetNotifications();
        var created = await s.Kit.Inquiry.CreateAsync(supplier, InquiryInput(email));
        var verification = s.Emails.Snapshot().Last(e => e.Template == EmailTemplates.Names.SupplierInquiryVerification);
        var token = Regex.Match(verification.Content.HtmlBody, $"inquiry={created.Id:D}#token=([A-Za-z0-9_-]+)").Groups[1].Value;
        var confirmation = await s.Kit.Inquiry.ConfirmEmailAsync(supplier, created.Id, token);
        s.ForgetNotifications();

        var supplierOrgId = supplier.OrgId;
        return await s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(r => r.PublicCode == confirmation.PublicCode && r.SupplierOrgId == supplierOrgId);
    }

    /// <summary>The messages of the thread of a request, as saved, the oldest first.</summary>
    public static Task<List<ServiceRequestMessage>> ThreadAsync(this ServiceRequestScenario s, Guid requestId, Guid? supplierOrgId = null) =>
        s.Db.ServiceRequestMessages
            .AsNoTracking()
            .Where(m => m.OrgId == (supplierOrgId ?? s.SupplierOrgId) && m.ServiceRequestId == requestId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();
}
