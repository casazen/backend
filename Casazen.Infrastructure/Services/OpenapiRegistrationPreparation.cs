using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Casazen.Infrastructure.Services;

/// <summary>The supplemental data stays encrypted on the lease; file fields reference only tenant-owned private attachments.</summary>
public sealed class OpenapiRegistrationPreparation(AppDbContext db, IFileStorage storage,
    IOptions<OpenapiOptions> options, IApeComplianceService apeCompliance) : IOpenapiRegistrationPreparation
{
    public async Task<OpenapiPreparationResult> GetAsync(Guid leaseId, CancellationToken ct = default)
    {
        var lease = await LoadAsync(leaseId, ct);
        var data = await DataAsync(lease, ct);
        return await ResultAsync(lease, data, ct);
    }

    public async Task<OpenapiPreparationResult> SaveAsync(Guid leaseId, OpenapiRegistrationData data, CancellationToken ct = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.OpenapiLeaseRegistrationCheckout, leaseId.ToString("N")));
        // Share the lease row lock used by provider submission: an in-flight filing freezes its supplement.
        if (db.Database.IsNpgsql())
            await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "LeaseContracts" WHERE "Id" = {leaseId} FOR UPDATE""").ToListAsync(ct);
        var lease = await LoadAsync(leaseId, ct);
        EnsureEditable(lease);
        if (data.Version != lease.OpenapiRegistrationVersion)
            throw new DomainConflictException("openapi_registration_conflict", "OpenapiRegistrationConflict");
        if (data.Values is null || data.Documents is null || data.Values.Count > 61 || data.Documents.Count > 6)
            throw new DomainRuleException("openapi_registration_invalid", "OpenapiRegistrationInvalid");
        // Invalid/incomplete values can be saved as a draft, but never raw URLs, file contents or unknown keys.
        var schema = OpenapiRegistrationSchema.Current;
        if (data.Values.Any(v => !schema.Fields.Any(f => f.Name == v.Key && f.Type != "file") || v.Value is null || v.Value.Length > 500)
            || data.Documents.Any(v => !schema.Fields.Any(f => f.Name == v.Key && f.Type == "file" && f.Name != "contractFile")))
            throw new DomainRuleException("openapi_registration_invalid", "OpenapiRegistrationInvalid");
        foreach (var key in data.Values.Keys.ToArray())
        {
            data.Values[key] = data.Values[key].Trim();
            if (key.EndsWith("TaxCode", StringComparison.Ordinal) || key is "ibanCode" or "province" or "territorialOffice")
                data.Values[key] = data.Values[key].Replace(" ", "").ToUpperInvariant();
            var f = schema.Fields.First(f => f.Name == key);
            if (f.Type == "float") data.Values[key] = data.Values[key].Replace(',', '.');
        }
        var attachments = await EnsureDocumentOwnershipAsync(lease, data, ct);
        foreach (var attachment in attachments.Where(d => d.Kind == LeaseDocumentKind.Other))
        {
            attachment.Kind = LeaseDocumentKind.RegistrationIdentity;
            attachment.ExtractionStatus = LeaseDocumentExtractionStatus.Skipped;
            attachment.ExtractionResult = null;
            attachment.ExtractionError = null;
        }
        lease.OpenapiRegistrationVersion++;
        data.Version = lease.OpenapiRegistrationVersion;
        lease.OpenapiRegistrationData = data.Serialize();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new DomainConflictException("openapi_registration_conflict", "OpenapiRegistrationConflict"); }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return await ResultAsync(lease, data, ct);
    }

    public async Task EnsureReadyAsync(LeaseContract lease, CancellationToken ct = default)
    {
        EnsureEditable(lease);
        if (!options.Value.IsConfigured || options.Value.LeaseDocumentId != OpenapiRegistrationSchema.Current.DocumentId
            || (options.Value.FieldOrderOrEmpty().Count > 0 && !options.Value.FieldOrderOrEmpty().SequenceEqual(OpenapiLeaseSearch.DefaultFieldOrder)))
            throw new DomainConflictException("openapi_registration_unavailable", "OpenapiRegistrationUnavailable");
        if (lease.OpenapiRegistrationData is null)
            throw new DomainRuleException("openapi_registration_incomplete", "OpenapiRegistrationIncomplete");
        var result = await ResultAsync(lease, await DataAsync(lease, ct), ct);
        if (!result.Ready) throw new DomainRuleException("openapi_registration_incomplete", "OpenapiRegistrationIncomplete");
        LeaseContractTerms.EnsureTerm(lease.ContractType, lease.StartDate, lease.EndDate);
        await apeCompliance.EnsurePropertyHasValidApeAsync(lease.PropertyId);
    }

    public async Task<IReadOnlyDictionary<string, string>> BuildSearchAsync(LeaseContract lease,
        ReadOnlyMemory<byte> signedContract, CancellationToken ct = default)
    {
        // Called after reservation too, when the lease is RegistrationPending. Validate the captured supplement and all files again.
        var data = await DataAsync(lease, ct);
        var result = await ResultAsync(lease, data, ct);
        if (result.Issues.Count > 0 || signedContract.IsEmpty)
            throw new DomainRuleException("openapi_registration_incomplete", "OpenapiRegistrationIncomplete");
        var documents = await EnsureDocumentOwnershipAsync(lease, data, ct);
        var files = new Dictionary<string, string>();
        long total = signedContract.Length;
        foreach (var reference in data.Documents)
        {
            var document = documents.Single(d => d.Id == reference.Value);
            await using var file = await storage.OpenReadAsync(StorageBucket.Private, document.StorageKey, ct)
                ?? throw new DomainRuleException("openapi_registration_incomplete", "OpenapiRegistrationIncomplete");
            using var buffer = new MemoryStream();
            var bytes = new byte[81920];
            int count;
            while ((count = await file.ReadAsync(bytes, ct)) > 0)
            {
                total += count;
                if (buffer.Length + count > LeaseDocumentService.MaxBytes || total > 80 * 1024 * 1024)
                    throw new DomainRuleException("openapi_registration_invalid", "OpenapiRegistrationInvalid");
                await buffer.WriteAsync(bytes.AsMemory(0, count), ct);
            }
            if (buffer.Length < 5 || !buffer.GetBuffer().AsSpan(0, 5).SequenceEqual("%PDF-"u8))
                throw new DomainRuleException("openapi_registration_invalid", "OpenapiRegistrationInvalid");
            files[reference.Key] = Convert.ToBase64String(buffer.ToArray());
        }
        return OpenapiLeaseSearch.Build(lease, signedContract, options.Value.FieldOrderOrEmpty(), files, data);
    }

    private async Task<LeaseContract> LoadAsync(Guid id, CancellationToken ct) => await db.LeaseContracts
        .Include(l => l.Property).Include(l => l.Parties).Include(l => l.Registration)
        .SingleOrDefaultAsync(l => l.Id == id, ct)
        ?? throw new NotFoundException("Lease not found") { Code = "lease_not_found" };

    private static void EnsureEditable(LeaseContract lease)
    {
        if (lease.Status != LeaseStatus.Signed || lease.PartiesAnonymizedAt is not null
            || lease.Registration?.Status is RegistrationStatus.Registered or RegistrationStatus.Pending or RegistrationStatus.SentToProvider)
            throw new DomainConflictException("openapi_registration_not_editable", "OpenapiRegistrationNotEditable");
    }

    private async Task<OpenapiRegistrationData> DataAsync(LeaseContract lease, CancellationToken ct)
    {
        var saved = OpenapiRegistrationData.Read(lease.OpenapiRegistrationData);
        var values = OpenapiLeaseSearch.Prefill(lease);
        foreach (var entry in saved.Values) values[entry.Key] = entry.Value;
        saved.Values = values;
        saved.Version = lease.OpenapiRegistrationVersion;
        if (!saved.Values.ContainsKey("contractPages") && !string.IsNullOrWhiteSpace(lease.SignedPdfStoragePath))
        {
            await using var file = await storage.OpenReadAsync(StorageBucket.Private, lease.SignedPdfStoragePath, ct);
            if (file is not null)
            {
                try
                {
                    using var buffer = new MemoryStream();
                    await file.CopyToAsync(buffer, ct);
                    using var pdf = PdfDocument.Open(buffer.ToArray());
                    saved.Values["contractPages"] = pdf.NumberOfPages.ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* The holder can enter the page count. */ }
            }
        }
        return saved;
    }

    private async Task<List<LeaseDocument>> EnsureDocumentOwnershipAsync(LeaseContract lease, OpenapiRegistrationData data, CancellationToken ct)
    {
        var ids = data.Documents.Values.ToArray();
        var documents = await db.LeaseDocuments.Where(d => d.LeaseContractId == lease.Id && d.OrgId == lease.OrgId && ids.Contains(d.Id)).ToListAsync(ct);
        if (ids.Any(id => documents.All(d => d.Id != id)))
            throw new DomainRuleException("openapi_registration_document_invalid", "OpenapiRegistrationDocumentInvalid");
        return documents;
    }

    private async Task<OpenapiPreparationResult> ResultAsync(LeaseContract lease, OpenapiRegistrationData data, CancellationToken ct)
    {
        var issues = OpenapiRegistrationValidation.Validate(data, !string.IsNullOrWhiteSpace(lease.SignedPdfStoragePath)).ToList();
        var ids = data.Documents.Values.ToArray();
        var documents = await db.LeaseDocuments.Where(d => d.LeaseContractId == lease.Id && d.OrgId == lease.OrgId && ids.Contains(d.Id)).ToListAsync(ct);
        if (data.Documents.Values.Sum(id => documents.SingleOrDefault(d => d.Id == id)?.SizeBytes ?? 0) > 60L * 1024 * 1024)
            issues.Add(new("landlordDocument", "Gli allegati di identità non possono superare complessivamente 60 MB."));
        foreach (var reference in data.Documents)
        {
            var doc = documents.SingleOrDefault(d => d.Id == reference.Value);
            if (doc is null) { issues.Add(new(reference.Key, "Seleziona un allegato disponibile di questo contratto.")); continue; }
            if (!await storage.ExistsAsync(StorageBucket.Private, doc.StorageKey, ct)) issues.Add(new(reference.Key, "Il file non è disponibile: caricalo nuovamente."));
        }
        if (!string.IsNullOrWhiteSpace(lease.SignedPdfStoragePath) && !await storage.ExistsAsync(StorageBucket.Private, lease.SignedPdfStoragePath, ct))
            issues.Add(new("contractFile", "Il contratto firmato non è disponibile."));
        if (lease.Parties.Count(p => p.Role == PartyRole.Landlord) > 3 || lease.Parties.Count(p => p.Role == PartyRole.Tenant) > 3)
            issues.Add(new("parties", "Il servizio supporta fino a tre locatori e tre conduttori. Per questo contratto usa la registrazione manuale."));
        var paid = await db.OpenapiLeaseRegistrationPayments.AnyAsync(p => p.LeaseContractId == lease.Id && p.OrgId == lease.OrgId
            && p.Status == OpenapiRegistrationPaymentStatus.Completed && p.AmountCents == OpenapiRegistrationFee.OpenapiServiceCents + OpenapiRegistrationFee.CasazenServiceCents && p.FeePercent == 0, ct);
        return new(OpenapiRegistrationSchema.Current, data, issues, issues.Count == 0, paid);
    }
}
