using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

internal static class InquiryScenario
{
    public const string Words = "Vorrei far sistemare la ringhiera del balcone e controllare la porta d'ingresso che si blocca.";

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

    public static Task<List<ServiceRequestMessage>> ThreadAsync(this ServiceRequestScenario s, Guid requestId, Guid? supplierOrgId = null) =>
        s.Db.ServiceRequestMessages
            .AsNoTracking()
            .Where(m => m.OrgId == (supplierOrgId ?? s.SupplierOrgId) && m.ServiceRequestId == requestId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToListAsync();
}
