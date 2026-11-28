using System.Text.RegularExpressions;
using Casazen.Core.Branding;

namespace Casazen.Core.Services;

/// <summary>
/// Builds and validates object keys of <see cref="IFileStorage"/>. One place for the layout:
/// <list type="bullet">
/// <item><c>properties/{propertyId}/photos/{file}</c> — public bucket</item>
/// <item><c>properties/{propertyId}/floorplans/{file}</c> — public bucket (DB-11a)</item>
/// <item><c>suppliers/{supplierOrgId}/photos/{file}</c> — public bucket</item>
/// <item><c>suppliers/{supplierOrgId}/showcase/{file}</c> — public bucket (logo, cover and photos of the jobs of the supplier's showcase, SP-13b)</item>
/// <item><c>properties/{propertyId}/documents/{file}</c> — private bucket</item>
/// <item><c>guest-documents/{orgId}/{guestId}/{file}</c> — private bucket</item>
/// <item><c>orgs/{orgId}/branding/{logo|hero}/{file}</c> — public bucket (BK-12)</item>
/// <item><c>service-requests/{requestId}/photos/{file}</c> — private bucket (photos of a supplier's work, SP-04)</item>
/// <item><c>orgs/{orgId}/leases/{leaseId}/maintenance/{requestId}/{file}</c> — private bucket (photos of a tenant maintenance request)</item>
/// <item><c>tourist-tax/{orgId}/{remittanceId}/{file}</c> — private bucket (the receipt of a tourist tax payment, NF-100)</item>
/// <item><c>accountant-packages/{orgId}/{yyyy-MM}.zip</c> — private bucket (the package of a month that waits for the link of its e-mail to the accountant, E15/F01/US02/T02)</item>
/// </list>
/// Private keys are what the database stores for private files (<c>PropertyDocument.StorageUrl</c>,
/// <c>Guest.DocumentScanUrl</c>); public files are stored as their absolute public URL.
/// </summary>
public static partial class StorageKeys
{
    public static string OwnerMandate(Guid orgId, Guid ownerId, string fileName) =>
        $"orgs/{orgId}/owners/{ownerId}/mandates/{fileName}";

    public static string LeaseAttachment(Guid orgId, Guid leaseId, string fileName) =>
        $"orgs/{orgId}/leases/{leaseId}/documents/{fileName}";

    public static string MaintenancePhoto(Guid orgId, Guid leaseId, Guid requestId, string fileName) =>
        EnsureValid($"orgs/{orgId}/leases/{leaseId}/maintenance/{requestId}/{fileName}");

    public static string PropertyPhoto(Guid propertyId, string fileName) =>
        $"properties/{propertyId}/photos/{fileName}";

    public static string PropertyFloorPlan(Guid propertyId, string fileName) =>
        $"properties/{propertyId}/floorplans/{fileName}";

    public static string SupplierPhoto(Guid supplierOrgId, string fileName) =>
        $"suppliers/{supplierOrgId}/photos/{fileName}";

    public static string SupplierFolder(Guid supplierOrgId) =>
        $"suppliers/{supplierOrgId}/";

    /// <summary>
    /// The folder of the images of a supplier's showcase (<c>suppliers/{supplierOrgId}/showcase/</c>, SP-13b), inside <see cref="SupplierFolder"/>:
    /// the logo, the cover and the photos of the jobs the supplier uploaded from the editor. A file here is one the upload stored (the content of
    /// a showcase may point only to those, never to a name somebody made up).
    /// </summary>
    public static string SupplierShowcaseFolder(Guid supplierOrgId) =>
        $"suppliers/{supplierOrgId}/showcase/";

    public static string SupplierShowcaseImage(Guid supplierOrgId, string fileName) =>
        $"suppliers/{supplierOrgId}/showcase/{fileName}";

    public static string PropertyDocument(Guid propertyId, string fileName) =>
        $"properties/{propertyId}/documents/{fileName}";

    public static string SwissComplianceReceipt(Guid orgId, Guid propertyId, Guid receiptId) =>
        $"orgs/{orgId}/properties/{propertyId}/swiss-receipts/{receiptId}.bin";

    public const string GuestDocumentPrefix = "guest-documents/";

    public static string GuestDocument(Guid orgId, Guid guestId, string fileName) =>
        $"{GuestDocumentPrefix}{orgId}/{guestId}/{fileName}";

    public static bool IsGuestDocument(string? key) =>
        key is not null && key.StartsWith(GuestDocumentPrefix, StringComparison.Ordinal);

    public static string OrgBrandingImage(Guid orgId, BrandingImageKind kind, string fileName) =>
        $"orgs/{orgId}/branding/{kind.ToString().ToLowerInvariant()}/{fileName}";

    public static string LeaseRegistrationReceipt(Guid orgId, Guid leaseId, string fileName) =>
        $"leases/{orgId}/{leaseId}/registration/{fileName}";

    public static string LeaseSignedContract(Guid orgId, Guid leaseId, string fileName) =>
        $"leases/{orgId}/{leaseId}/signed-contract/{fileName}";

    /// <summary>Receipt of the Questura communication for an extra-EU tenant (private bucket, LT-07).</summary>
    public static string LeaseQuesturaReceipt(Guid orgId, Guid leaseId, string fileName) =>
        $"leases/{orgId}/{leaseId}/questura/{fileName}";

    public static string ServiceRequestPhoto(Guid serviceRequestId, string fileName) =>
        $"service-requests/{serviceRequestId}/photos/{fileName}";

    public static string TouristTaxReceipt(Guid orgId, Guid remittanceId, string fileName) =>
        $"tourist-tax/{orgId}/{remittanceId}/{fileName}";

    /// <summary>
    /// The file of a proof the host uploaded for the compliance traffic light (private bucket, E03/F02/US01, NF-47, NF-52): the receipt of an Alloggiati,
    /// ISTAT or tourist tax communication. Never reached by a public URL. <paramref name="fileName"/> is made by the server, never the one the host sent.
    /// </summary>
    public static string ManualComplianceProof(Guid orgId, Guid proofId, string fileName) =>
        $"compliance-proofs/{orgId}/{proofId}/{fileName}";

    /// <summary>
    /// A file of a document an agency shares with one of its owners (private bucket, E07/F07/US01/T02): read only through the endpoints that check the owner,
    /// the agency and the permission, never by a public URL. <paramref name="fileName"/> is made by the server (the id of the version and the extension the
    /// scan recognised), never the name sent.
    /// </summary>
    public static string OwnerDocumentFile(Guid orgId, Guid documentId, string fileName) =>
        $"owner-documents/{orgId}/{documentId}/{fileName}";

    public static string OwnerMessageAttachment(Guid orgId, Guid conversationId, string fileName) =>
        $"owner-messages/{orgId}/{conversationId}/{fileName}";

    /// <summary>
    /// The package of the accountant of one month (private bucket, E15/F01/US02/T02, NF-101): kept until the link of the e-mail that names it has expired, and opened only through
    /// <c>GET api/public/accountant-package/{orgId}/{period}</c> with a signed link, never by a public URL. The key is made of the org and the month and nothing else, so one org has at most one
    /// object for a month and the next send of the same month replaces it. <paramref name="period"/> is <c>yyyy-MM</c> (anything else is refused).
    /// </summary>
    public static string AccountantPackage(Guid orgId, string period) =>
        EnsureValid($"accountant-packages/{orgId:D}/{period}.zip");

    public static string NewFileName(string? originalFileName) =>
        $"{Guid.NewGuid()}{Path.GetExtension(originalFileName ?? string.Empty).ToLowerInvariant()}";

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024 || !SafeKey().IsMatch(key))
            return false;

        return key.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    public static string EnsureValid(string? key) =>
        IsValid(key) ? key! : throw new ArgumentException("Invalid storage key.", nameof(key));

    public static string ContentTypeFor(string? fileName) =>
        Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    [GeneratedRegex("^[A-Za-z0-9._/-]+$")]
    private static partial Regex SafeKey();
}
