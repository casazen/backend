using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casazen.Tests.Unit.Services;

/// <summary>A private bucket in memory: what the sources of the document archive open (<c>StorageBucket.Private</c> only).</summary>
internal sealed class InMemoryFileStorage : IFileStorage
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public void Add(string key, byte[] content) => _objects[key] = content;

    public bool Remove(string key) => _objects.Remove(key);

    public Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var copy = new MemoryStream();
        content.CopyTo(copy);
        _objects[key] = copy.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(
            bucket == StorageBucket.Private && _objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(bucket == StorageBucket.Private && _objects.ContainsKey(key));

    public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
    {
        _objects.Remove(key);
        return Task.CompletedTask;
    }

    public string GetPublicUrl(string key) => throw new NotSupportedException("The archive reads the private bucket only.");

    public string? TryGetPublicKey(string url) => null;

    public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);
}

/// <summary>
/// The data the tests of the document archive run on (E07/F03/US03): the org of <see cref="HostScopeScenario"/> with two properties (the
/// collaborator «Solo alcuni» was given only the first), another org that nobody here may see, and for each property one property document, one
/// file attached to its lease, and for each org one tourist tax payment with a receipt. Every file is in the private bucket under the key the
/// real code builds. The InMemory database has no tenant context (the system), so only the explicit predicates of the sources can keep the
/// orgs apart. The rows have a different day each, so the order of the list is the one the assertions name.
/// </summary>
internal sealed class DocumentArchiveWorld
{
    public static readonly DateTime GrantedPropertyDay = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime HiddenPropertyDay = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime OtherOrgPropertyDay = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime GrantedLeaseDay = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime HiddenLeaseDay = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime OtherOrgLeaseDay = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
    public static readonly DateOnly TaxReceiptDay = new(2026, 8, 15);
    public static readonly DateOnly TaxWithoutReceiptDay = new(2026, 8, 16);
    public static readonly DateOnly OtherOrgTaxDay = new(2026, 8, 17);

    /// <summary>Every type the three sources serve.</summary>
    public static DocumentArchiveAccess Everything { get; } = new(
        new HashSet<ArchiveDocumentType> { ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract, ArchiveDocumentType.TouristTax });

    private DocumentArchiveWorld(AppDbContext db, HostScopeWorld hosts, InMemoryFileStorage storage)
    {
        Db = db;
        Hosts = hosts;
        Storage = storage;
    }

    public AppDbContext Db { get; }

    public HostScopeWorld Hosts { get; }

    public InMemoryFileStorage Storage { get; }

    public Guid GrantedPropertyDocument { get; private set; }

    public Guid HiddenPropertyDocument { get; private set; }

    public Guid OtherOrgPropertyDocument { get; private set; }

    public Guid GrantedLeaseDocument { get; private set; }

    public Guid HiddenLeaseDocument { get; private set; }

    public Guid OtherOrgLeaseDocument { get; private set; }

    public Guid TaxReceipt { get; private set; }

    public Guid TaxWithoutReceipt { get; private set; }

    public Guid OtherOrgTaxReceipt { get; private set; }

    /// <summary>The storage key of the file of a document, to take it away from the bucket.</summary>
    public Dictionary<Guid, string> KeysByDocument { get; } = [];

    public static async Task<DocumentArchiveWorld> CreateAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var hosts = await HostScopeScenario.SeedAsync(db);
        var world = new DocumentArchiveWorld(db, hosts, new InMemoryFileStorage());
        await world.SeedDocumentsAsync();
        return world;
    }

    public static byte[] Pdf(string text) => Encoding.ASCII.GetBytes($"%PDF-1.4 {text}");

    /// <summary>The sources as the container registers them, over this database and this bucket.</summary>
    public IReadOnlyList<IDocumentArchiveSource> Sources() =>
    [
        new PropertyDocumentArchiveSource(Db, Storage),
        new LeaseDocumentArchiveSource(Db, Storage),
        new TouristTaxRemittanceArchiveSource(Db, Storage),
    ];

    public DocumentArchiveService Service(DocumentArchiveOptions? limits = null) =>
        new(Sources(), Db, Options.Create(limits ?? new DocumentArchiveOptions()), NullLogger<DocumentArchiveService>.Instance);

    /// <summary>
    /// More documents of the granted property, as an upload stores them: no size, and a file in the bucket (one more file name each time).
    /// </summary>
    public async Task AddPropertyDocumentsAsync(int count)
    {
        for (var number = 0; number < count; number++)
            AddPropertyDocument(Hosts.Granted, $"extra-{Guid.NewGuid():N}.pdf", GrantedPropertyDay);

        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    private async Task SeedDocumentsAsync()
    {
        GrantedPropertyDocument = AddPropertyDocument(Hosts.Granted, "APE-Trullo.pdf", GrantedPropertyDay);
        HiddenPropertyDocument = AddPropertyDocument(Hosts.Hidden, "Licenza-Casa-Bianca.pdf", HiddenPropertyDay);
        OtherOrgPropertyDocument = AddPropertyDocument(Hosts.OtherOrgProperty, "Altrove.pdf", OtherOrgPropertyDay);
        GrantedLeaseDocument = await AddLeaseDocumentAsync(Hosts.Granted, "contratto-trullo.pdf", GrantedLeaseDay);
        HiddenLeaseDocument = await AddLeaseDocumentAsync(Hosts.Hidden, "contratto-casa-bianca.pdf", HiddenLeaseDay);
        OtherOrgLeaseDocument = await AddLeaseDocumentAsync(Hosts.OtherOrgProperty, "contratto-altrove.pdf", OtherOrgLeaseDay);
        TaxReceipt = AddRemittance(Hosts.OrgId, "Ostuni", TaxReceiptDay, withReceipt: true);
        TaxWithoutReceipt = AddRemittance(Hosts.OrgId, "Brindisi", TaxWithoutReceiptDay, withReceipt: false);
        OtherOrgTaxReceipt = AddRemittance(Hosts.OtherOrgId, "Milano", OtherOrgTaxDay, withReceipt: true);

        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    private Guid AddPropertyDocument(Property property, string fileName, DateTime uploadedAt)
    {
        var document = new PropertyDocument
        {
            PropertyId = property.Id,
            OrgId = property.OrgId,
            FileName = fileName,
            StorageUrl = StorageKeys.PropertyDocument(property.Id, fileName),
            DocumentType = DocumentType.Other,
            UploadedBy = "auth0|host",
            UploadedAt = uploadedAt,
        };
        Db.PropertyDocuments.Add(document);
        Storage.Add(document.StorageUrl, Pdf(fileName));
        KeysByDocument[document.Id] = document.StorageUrl;
        return document.Id;
    }

    private async Task<Guid> AddLeaseDocumentAsync(Property property, string fileName, DateTime uploadedAt)
    {
        var leaseId = await Db.LeaseContracts.Where(l => l.PropertyId == property.Id).Select(l => l.Id).SingleAsync();
        var document = new LeaseDocument
        {
            LeaseContractId = leaseId,
            OrgId = property.OrgId,
            Kind = LeaseDocumentKind.Contract,
            FileName = fileName,
            StorageKey = StorageKeys.LeaseAttachment(property.OrgId, leaseId, StorageKeys.NewFileName(".pdf")),
            UploadedBy = "auth0|host",
            UploadedAt = uploadedAt,
        };
        var content = Pdf(fileName);
        document.SizeBytes = content.Length;
        Db.LeaseDocuments.Add(document);
        Storage.Add(document.StorageKey, content);
        KeysByDocument[document.Id] = document.StorageKey;
        return document.Id;
    }

    private Guid AddRemittance(Guid orgId, string comune, DateOnly paidOn, bool withReceipt)
    {
        var id = Guid.NewGuid();
        var remittance = new TouristTaxRemittance
        {
            Id = id,
            OrgId = orgId,
            Comune = comune,
            ComuneKey = comune.ToLowerInvariant(),
            PeriodFrom = paidOn.AddDays(-90),
            PeriodTo = paidOn.AddDays(-60),
            PaidAmount = 18.00m,
            PaidOn = paidOn,
            RecordedByUserId = "auth0|host",
        };
        if (withReceipt)
        {
            var content = Pdf($"ricevuta {comune}");
            remittance.ReceiptStorageKey = StorageKeys.TouristTaxReceipt(orgId, id, $"{Guid.NewGuid()}.pdf");
            remittance.ReceiptFileName = $"ricevuta-{comune.ToLowerInvariant()}.pdf";
            remittance.ReceiptContentType = "application/pdf";
            remittance.ReceiptSizeBytes = content.Length;
            Storage.Add(remittance.ReceiptStorageKey, content);
            KeysByDocument[id] = remittance.ReceiptStorageKey;
        }

        Db.TouristTaxRemittances.Add(remittance);
        return id;
    }
}
