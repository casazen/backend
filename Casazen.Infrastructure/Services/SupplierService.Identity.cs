using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public partial class SupplierService
{
    private Task<IDbContextTransaction?> LockHostInviteAsync(Guid host, string email, CancellationToken ct) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.SupplierInvite, host.ToString("N") + ":" + SupplierProfileEmailIndex.Normalize(email)));

    private sealed record IdentityMatch(Guid Id, bool Published, bool EmailMatch, bool VatMatch, bool FiscalMatch,
        bool PhoneMatch, bool HasVat, bool HasFiscal, bool HasPhone)
    {
        public IEnumerable<string> Reasons()
        {
            if (EmailMatch) yield return "email";
            if (VatMatch) yield return "vat";
            if (FiscalMatch) yield return "fiscal";
            if (PhoneMatch) yield return "phone";
        }
    }

    private sealed record ReviewEvidence(SupplierInviteIdentity Supplied, IReadOnlyList<IdentityMatch> Matches);

    private static SupplierInviteIdentity NormalizeIdentity(SupplierInviteIdentity? value)
    {
        static string? Compact(string? input) => string.IsNullOrWhiteSpace(input) ? null :
            input.Trim().Replace(" ", "").Replace(".", "").Replace("-", "").ToUpperInvariant();

        var vat = Compact(value?.VatNumber);
        var fiscal = Compact(value?.FiscalCode);
        var phone = CrmNormalizer.NormalizePhone(value?.Phone);
        if (vat is not null && !Regex.IsMatch(vat, "^[A-Z0-9]{5,28}$") ||
            fiscal is not null && !Regex.IsMatch(fiscal, "^(?:[0-9]{11}|[A-Z0-9]{16})$") ||
            !string.IsNullOrWhiteSpace(value?.Phone) && (phone == null || value.Phone.Any(char.IsLetter)))
            throw new DomainRuleException("supplier_invite_identity_invalid", "SupplierInviteIdentityInvalid");
        return new SupplierInviteIdentity(vat, fiscal, phone);
    }

    private async Task<List<IdentityMatch>> FindIdentityMatchesAsync(string email, SupplierInviteIdentity supplied, CancellationToken ct)
    {
        var normalizedEmail = SupplierProfileEmailIndex.Normalize(email);
        if (supplied is { VatNumber: null, FiscalCode: null, Phone: null })
            return await db.SupplierProfiles.AsNoTracking().Where(p => p.Email.Trim().ToLower() == normalizedEmail)
                .Select(p => new IdentityMatch(p.OrgId, p.GuestPortalMarketplaceEnabled, true, false, false, false,
                    false, false, false)).ToListAsync(ct);

        var vat = supplied.VatNumber;
        // Only Italy's eleven-digit number is equivalent with and without the IT prefix.
        var italianVat = vat is null ? null : Regex.Match(vat, "^(?:IT)?([0-9]{11})$");
        var vatForms = italianVat?.Success == true ? new[] { italianVat.Groups[1].Value, "IT" + italianVat.Groups[1].Value }
            : vat is null ? Array.Empty<string>() : [vat];
        var phone = supplied.Phone;
        var phoneForms = phone is null ? [] : phone.StartsWith("+39", StringComparison.Ordinal)
            ? new[] { phone, "00" + phone[1..], phone[3..] } : [phone, "00" + phone[1..]];
        var fiscal = supplied.FiscalCode;

        // Filter at the database. Unmatched profiles, phone numbers and tax identifiers never leave it.
        var matches = from profile in db.SupplierProfiles.AsNoTracking()
                      let vat1 = (profile.VatNumber ?? "").Trim().ToUpper().Replace(" ", "").Replace(".", "").Replace("-", "")
                      let vat2 = (profile.Org.VatId ?? "").Trim().ToUpper().Replace(" ", "").Replace(".", "").Replace("-", "")
                      let vat3 = (profile.Org.PartitaIvaNumber ?? "").Trim().ToUpper().Replace(" ", "")
                      let fiscal1 = (profile.Org.FiscalCode ?? "").Trim().ToUpper().Replace(" ", "")
                      let fiscal2 = (profile.Org.BillingFiscalCode ?? "").Trim().ToUpper().Replace(" ", "")
                      let telephone = profile.Phone.Trim().Replace("(0)", "").Replace(" ", "").Replace(".", "")
                          .Replace("-", "").Replace("(", "").Replace(")", "").Replace("/", "")
                      let emailMatch = profile.Email.Trim().ToLower() == normalizedEmail
                      let vatMatch = vat != null && (vatForms.Contains(vat1) || vatForms.Contains(vat2) || vatForms.Contains(vat3))
                      let fiscalMatch = fiscal != null && (fiscal1 == fiscal || fiscal2 == fiscal)
                      let phoneMatch = phone != null && phoneForms.Contains(telephone)
                      where emailMatch || vatMatch || fiscalMatch || phoneMatch
                      orderby profile.OrgId
                      select new IdentityMatch(profile.OrgId, profile.GuestPortalMarketplaceEnabled,
                          emailMatch, vatMatch, fiscalMatch, phoneMatch, vat1 != "" || vat2 != "" || vat3 != "",
                          fiscal1 != "" || fiscal2 != "", telephone != "");
        return await matches.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SupplierIdentityReview>> ListIdentityReviewsAsync(int page, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await (from invite in db.SupplierInviteRecords.AsNoTracking()
                          join host in db.Orgs on invite.InvitedByOrgId equals host.Id
                          where invite.IdentityReviewJson != null && invite.RevokedAt == null && !invite.IsUsed && invite.ExpiresAt > now
                          orderby invite.CreatedAt, invite.Id
                          select new { invite.Id, invite.Email, invite.ExpiresAt, invite.IdentityReviewJson, Host = host.Name })
            .Skip((page - 1) * 25).Take(25).ToListAsync(ct);
        var reviews = rows.Select(r => (Row: r, Evidence: JsonSerializer.Deserialize<ReviewEvidence>(r.IdentityReviewJson!, JsonOpts)!)).ToList();
        var ids = reviews.SelectMany(r => r.Evidence.Matches.Select(m => m.Id)).Distinct().ToArray();
        var found = await db.SupplierProfiles.AsNoTracking().Where(p => ids.Contains(p.OrgId))
            .Select(p => new { p.OrgId, p.LegalName, p.Email }).ToDictionaryAsync(p => p.OrgId, ct);
        return reviews.Select(r => new SupplierIdentityReview(r.Row.Id, r.Row.Host, r.Row.Email, r.Evidence.Supplied,
            r.Row.ExpiresAt, r.Evidence.Matches.Where(m => found.ContainsKey(m.Id)).Select(m =>
                new SupplierIdentityCandidate(m.Id, found[m.Id].LegalName, found[m.Id].Email, m.Reasons().ToList())).ToList())).ToList();
    }

    public async Task ResolveIdentityReviewAsync(Guid inviteId, string decision, Guid? supplierOrgId, string actor, CancellationToken ct)
    {
        var invite = await db.SupplierInviteRecords.SingleOrDefaultAsync(i => i.Id == inviteId, ct)
            ?? throw new NotFoundException("Supplier invite not found");
        if (invite.IdentityReviewJson is null || invite.IsUsed || invite.RevokedAt != null || invite.ExpiresAt <= DateTime.UtcNow)
            throw new DomainConflictException("supplier_identity_review_closed", "SupplierIdentityReviewClosed");

        var evidence = JsonSerializer.Deserialize<ReviewEvidence>(invite.IdentityReviewJson, JsonOpts)!;
        if (decision == "existing" && supplierOrgId is { } id)
        {
            var match = evidence.Matches.SingleOrDefault(m => m.Id == id);
            var recipient = match is null ? null : await db.SupplierProfiles.Where(p => p.OrgId == id)
                .Select(p => p.Email).SingleOrDefaultAsync(ct);
            if (recipient is null)
                throw new DomainRuleException("supplier_identity_choice_invalid", "SupplierIdentityChoiceInvalid");
            // The invitation travels only to the profile owner. Acceptance still checks account, token and the same org.
            invite.Email = recipient;
            invite.IdentifiedSupplierOrgId = id;
            invite.HostOriginAtInvitation = !match!.Published;
        }
        else if (decision == "distinct" && supplierOrgId == null && !await IsSupplierEmailTakenAsync(invite.Email, ct))
        {
            invite.IdentifiedSupplierOrgId = null;
            invite.HostOriginAtInvitation = true;
        }
        else if (decision == "reject" && supplierOrgId == null)
            invite.RevokedAt = DateTime.UtcNow;
        else
            throw new DomainRuleException("supplier_identity_choice_invalid", "SupplierIdentityChoiceInvalid");

        await using var tx = await LockHostInviteAsync(invite.InvitedByOrgId!.Value, invite.Email, ct);
        var now = DateTime.UtcNow;
        var normalizedRecipient = SupplierProfileEmailIndex.Normalize(invite.Email);
        if (invite.RevokedAt == null && (await db.SupplierInviteRecords.AnyAsync(i =>
            i.Id != invite.Id && i.InvitedByOrgId == invite.InvitedByOrgId && i.Email.ToLower() == normalizedRecipient &&
            i.TokenHash != null && i.RevokedAt == null && !i.IsUsed && i.ExpiresAt > now, ct) ||
            (invite.IdentifiedSupplierOrgId != null && await db.TrustedSuppliers.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(t => t.OrgId == invite.InvitedByOrgId && t.SupplierOrgId == invite.IdentifiedSupplierOrgId, ct))))
            invite.RevokedAt = now;

        invite.IdentityReviewJson = null;
        try
        {
            if (invite.RevokedAt != null)
            {
                await db.SaveChangesAsync(ct);
                if (tx != null) await tx.CommitAsync(ct);
            }
            else
                await IssueInviteAsync(invite, await ResolveComuneNameAsync(invite.ComuneCode, ct), ct, tx);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainConflictException("supplier_identity_review_closed", "SupplierIdentityReviewClosed");
        }
        logger.LogInformation("Supplier invite {InviteId} identity review resolved by {Actor} with decision {Decision}",
            invite.Id, actor, decision);
    }
}
