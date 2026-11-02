using System.Linq.Expressions;
using Casazen.Core.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Data.Encryption;

/// <summary>
/// The list of the columns encrypted at rest (CO-14, A5-30), on the one mechanism introduced by FD-07 and PC-11: the
/// Data Protection value converter <see cref="EncryptedStringConverter"/> (one purpose per column family, values stored
/// in clear before the encryption still readable) and the EF model cached per provider
/// (<see cref="DataProtectionModelCacheKeyFactory"/>). Every encrypted column is declared here and nowhere else:
/// <see cref="AppDbContext"/> gives each one its converter, <see cref="EncryptLegacyPlaintextAsync"/> rewrites the values
/// still in clear at startup. Code reads and writes the clear value through EF; the database only holds the payload.
/// Runbook: <c>docs/runbooks/encryption.md</c>.
/// </summary>
/// <remarks>
/// A Data Protection payload carries the id of the key that produced it: after a key rotation (automatic every 90 days,
/// or forced) old values stay readable with their key and new writes use the new default key. The key ring lives in
/// the <c>DataProtectionKeys</c> table, encrypted with the certificate of the environment (FD-07, CO-14).
/// </remarks>
public static class EncryptedColumns
{
    /// <summary>Purpose of the OTA partner credentials (FD-07). Never change a purpose: stored values would become unreadable.</summary>
    public const string OtaSecretsPurpose = "Casazen.OtaIntegration.Secrets";

    /// <summary>
    /// Purpose of the identity document fields of the guests, shared by <see cref="Guest"/> and <see cref="StayGuest"/>
    /// so that a payload copied from one table to the other (e.g. by a SQL backfill) stays readable.
    /// </summary>
    public const string GuestDocumentPurpose = "Casazen.Guest.Document";

    /// <summary>Purpose of the Alloggiati Web credentials of a property.</summary>
    public const string QuesturaCredentialsPurpose = "Casazen.PropertyQuesturaCredentials";

    /// <summary>
    /// Purpose of the personal data of the private customers of the suppliers (SP-10): name, e-mail and phone of
    /// <see cref="ServiceCustomer"/>, and the payload of the booking that waits for the e-mail check
    /// (<see cref="ShowcaseBookingHold"/>), which is the same data before it is a customer. Unlike the guests of the hosts, nothing
    /// here is searched in clear: the address is found by its HMAC (<see cref="ServiceCustomer.EmailHash"/>).
    /// </summary>
    public const string ServiceCustomerPurpose = "Casazen.ServiceCustomer";

    /// <summary>
    /// Purpose of the words of the thread of a request on measure (SP-10b): <see cref="ServiceRequestMessage.Body"/>, free text a person
    /// wrote for the other party. Its own purpose, because it is read by the two parties of the request and by nobody else. Never change it.
    /// </summary>
    public const string ServiceRequestMessagePurpose = "Casazen.ServiceRequestMessage";

    /// <summary>
    /// Purpose of the place of the work of a request from the public showcase (SP-10): street address, floor and access notes of
    /// <see cref="ServiceRequest"/>. The comune and the postal code are not encrypted: the supplier sees them before it takes the request.
    /// </summary>
    public const string ServiceRequestLocationPurpose = "Casazen.ServiceRequest.Location";

    /// <summary>
    /// Purpose of the words of a complaint about a job paid through CasaZen (SP-15c-c): what the payer wrote, what the supplier answered and the
    /// note of the admin who decided (<see cref="ServicePaymentDispute"/>). Free text about a person's home and a job done in it; read by the people
    /// it concerns and by the admins, never searched. Never change it.
    /// </summary>
    public const string ServicePaymentDisputePurpose = "Casazen.ServicePaymentDispute";

    /// <summary>Purpose of the AI provider key saved from the admin panel. Never change it: stored keys would become unreadable.</summary>
    public const string PlatformAiSettingsPurpose = "Casazen.PlatformAiSettings";

    /// <summary>
    /// Purpose of the typed state of a lease draft (LR-12): <see cref="LeaseContract.DraftData"/>, the JSON with the names, fiscal codes and
    /// residences the landlord typed in the wizard, kept until the data of the draft is complete. Nothing is searched in it. Never change it.
    /// </summary>
    public const string LeaseDraftPurpose = "Casazen.LeaseContract.Draft";

    /// <summary>
    /// Purpose of the personal data of a party of a lease that is not already masked and hashed elsewhere (LR-12): the residence of
    /// <see cref="Party"/>. The name, the fiscal code and the e-mail of a party stay as they have always been (clear in the table, masked in every
    /// DTO); the residence is new, and is never returned. Never change it.
    /// </summary>
    public const string LeasePartyPurpose = "Casazen.LeaseParty";

    /// <summary>
    /// Purpose of the need of a transitory lease (TR): <see cref="LeaseContract.TransitoryNeed"/> and <see cref="LeaseContract.TransitoryDocumentation"/>,
    /// the reason the landlord or the tenant gives for the short term and the papers that prove it. Free text that can say why a person moves
    /// house; printed in the contract, never searched. Never change it.
    /// </summary>
    public const string LeaseTransitoryNeedPurpose = "Casazen.LeaseContract.TransitoryNeed";

    /// <summary>
    /// Purpose of the identity document image URLs (NF-10, ADR-009-R21): <see cref="DocumentImage.FrontImageUrl"/> and
    /// <see cref="DocumentImage.BackImageUrl"/>. Never stored for Ticino properties per ADR-013-R6. Never change it.
    /// </summary>
    public const string DocumentImagePurpose = "Casazen.DocumentImage";

    /// <summary>
    /// Start of every Data Protection payload (base64url of its magic header <c>09 F0 C9 F0</c>). A stored value without
    /// it was written in clear before its column was encrypted.
    /// </summary>
    public const string ProtectedPayloadPrefix = "CfDJ8";

    private const int BatchSize = 200;

    /// <summary>SQL-translatable twin of <see cref="IsLegacyPlaintext"/>: <c>col NOT LIKE 'CfDJ8%'</c>.</summary>
    private static readonly Expression<Func<string, bool>> NotAPayload =
        stored => !stored.StartsWith(ProtectedPayloadPrefix);

    private static readonly IEncryptedEntity[] Entities =
    [
        new EncryptedEntity<OtaIntegration>(
            OtaSecretsPurpose,
            nameof(OtaIntegration.ApiKey),
            nameof(OtaIntegration.ApiSecret)),
        // iCal import URLs (PC-11, A2-20): only a URL is accepted as a value stored in clear.
        new EncryptedEntity<PropertyICalFeed>(
            PropertyICalFeedUrlEncryption.Purpose,
            PropertyICalFeedUrlEncryption.IsLegacyPlaintext,
            stored => stored.ToLower().StartsWith("https://") || stored.ToLower().StartsWith("http://"),
            nameof(PropertyICalFeed.ImportUrl)),
        new EncryptedEntity<Guest>(
            GuestDocumentPurpose,
            nameof(Guest.DocumentNumber),
            nameof(Guest.DocumentIssuingCountry)),
        new EncryptedEntity<StayGuest>(
            GuestDocumentPurpose,
            nameof(StayGuest.DocumentNumber),
            nameof(StayGuest.DocumentIssuePlaceName)),
        new EncryptedEntity<PropertyQuesturaCredentials>(
            QuesturaCredentialsPurpose,
            nameof(PropertyQuesturaCredentials.Username),
            nameof(PropertyQuesturaCredentials.Password),
            nameof(PropertyQuesturaCredentials.WsKey)),
        // ADR-012: property owner financial data (IBAN and fiscal code).
        new EncryptedEntity<PropertyOwner>(
            PropertyOwnerEncryptionPurpose.Purpose,
            nameof(PropertyOwner.Iban),
            nameof(PropertyOwner.FiscalCode)),
        // SP-10: the private customers of the suppliers and the booking that waits for their e-mail check.
        new EncryptedEntity<ServiceCustomer>(
            ServiceCustomerPurpose,
            nameof(ServiceCustomer.FullName),
            nameof(ServiceCustomer.Email),
            nameof(ServiceCustomer.Phone)),
        new EncryptedEntity<ShowcaseBookingHold>(
            ServiceCustomerPurpose,
            nameof(ShowcaseBookingHold.Payload)),
        // SP-10b: the request on measure that waits for the e-mail check (the same data as the hold, plus the words) and the thread.
        new EncryptedEntity<ShowcaseInquiry>(
            ServiceCustomerPurpose,
            nameof(ShowcaseInquiry.Payload)),
        new EncryptedEntity<ServiceRequestMessage>(
            ServiceRequestMessagePurpose,
            nameof(ServiceRequestMessage.Body)),
        new EncryptedEntity<ServiceRequest>(
            ServiceRequestLocationPurpose,
            nameof(ServiceRequest.LocationAddress),
            nameof(ServiceRequest.LocationFloor),
            nameof(ServiceRequest.LocationAccessNotes)),
        // SP-15c-c: the words of a complaint about a job paid through CasaZen.
        new EncryptedEntity<ServicePaymentDispute>(
            ServicePaymentDisputePurpose,
            nameof(ServicePaymentDispute.CustomerText),
            nameof(ServicePaymentDispute.SupplierReply),
            nameof(ServicePaymentDispute.AdminNote)),
        new EncryptedEntity<PlatformAiSettings>(
            PlatformAiSettingsPurpose,
            nameof(PlatformAiSettings.ApiKey)),
        // LR-12: the typed state of a lease draft and the residence of a party.
        new EncryptedEntity<LeaseContract>(
            LeaseDraftPurpose,
            nameof(LeaseContract.DraftData)),
        new EncryptedEntity<Party>(
            LeasePartyPurpose,
            nameof(Party.Residence)),
        // TR: the need of a transitory lease and its documentation.
        new EncryptedEntity<LeaseContract>(
            LeaseTransitoryNeedPurpose,
            nameof(LeaseContract.TransitoryNeed),
            nameof(LeaseContract.TransitoryDocumentation)),
        // NF-10: identity document image URLs for OCR (ADR-009-R21, ADR-013-R3).
        new EncryptedEntity<DocumentImage>(
            DocumentImagePurpose,
            nameof(DocumentImage.FrontImageUrl),
            nameof(DocumentImage.BackImageUrl)),
        // NF-13: property access codes (E06/F03/US01/T01).
        new EncryptedEntity<PropertyAccessCode>(
            PropertyAccessCodeEncryptionPurpose.Purpose,
            nameof(PropertyAccessCode.EncryptedCode)),
    ];

    /// <summary>Every encrypted column: entity, property and Data Protection purpose.</summary>
    public static IReadOnlyList<EncryptedColumn> All { get; } =
        Entities.SelectMany(e => e.Properties.Select(p => new EncryptedColumn(e.EntityType, p, e.Purpose))).ToList();

    /// <summary>
    /// True for a stored value written in clear before its column was encrypted: it is read as it is until
    /// <see cref="EncryptLegacyPlaintextAsync"/> rewrites it (every write is encrypted).
    /// </summary>
    public static bool IsLegacyPlaintext(string stored) =>
        !stored.StartsWith(ProtectedPayloadPrefix, StringComparison.Ordinal);

    /// <summary>Gives every encrypted column its converter, built from <paramref name="provider"/>.</summary>
    internal static void Configure(ModelBuilder modelBuilder, IDataProtectionProvider provider)
    {
        foreach (var entity in Entities)
            entity.Configure(modelBuilder, provider);
    }

    /// <summary>True when the model of <paramref name="db"/> encrypts every column of <see cref="All"/>.</summary>
    public static bool IsConfigured(AppDbContext db) =>
        All.All(c => db.Model.FindEntityType(c.EntityType)?.FindProperty(c.Property)?.GetValueConverter()
            is EncryptedStringConverter);

    /// <summary>
    /// Rewrites, encrypted, the values still stored in clear (written before their column was encrypted). Idempotent:
    /// run at every startup after the migrations, a no-op once done; returns the number of rows rewritten. Rows are
    /// found on the stored text (a context without converters) and rewritten through the EF converter of
    /// <paramref name="db"/>, the same one every later read uses; only the encrypted columns are updated.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="db"/> has no Data Protection: the application must not run while it would store these values in clear.
    /// </exception>
    public static async Task<int> EncryptLegacyPlaintextAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (!IsConfigured(db))
        {
            throw new InvalidOperationException(
                "The encrypted columns are not encrypted: AppDbContext was built without Data Protection " +
                "(docs/runbooks/encryption.md).");
        }

        // Same database and options, no provider: its model has no converters, so it reads the stored text.
        var options = (DbContextOptions<AppDbContext>)db.GetService<IDbContextOptions>();
        await using var stored = new AppDbContext(options);

        var total = 0;
        foreach (var entity in Entities)
        {
            var rewritten = await entity.EncryptLegacyPlaintextAsync(stored, db, ct);
            if (rewritten > 0)
            {
                logger.LogInformation(
                    "Encrypted {Count} {Entity} rows stored in clear", rewritten, entity.EntityType.Name);
            }

            total += rewritten;
        }

        return total;
    }

    private interface IEncryptedEntity
    {
        Type EntityType { get; }

        string Purpose { get; }

        IReadOnlyList<string> Properties { get; }

        void Configure(ModelBuilder modelBuilder, IDataProtectionProvider provider);

        Task<int> EncryptLegacyPlaintextAsync(AppDbContext stored, AppDbContext db, CancellationToken ct);
    }

    /// <param name="purpose">Data Protection purpose of the columns.</param>
    /// <param name="isLegacyPlaintext">Stored values read back as they are (written in clear before the encryption).</param>
    /// <param name="storedInClear">The same test, translatable to SQL, to find the rows to rewrite.</param>
    /// <param name="properties">Encrypted string properties of the entity.</param>
    private sealed class EncryptedEntity<TEntity>(
        string purpose,
        Func<string, bool> isLegacyPlaintext,
        Expression<Func<string, bool>> storedInClear,
        params string[] properties) : IEncryptedEntity
        where TEntity : class
    {
        /// <summary>Columns whose values stored in clear are the ones without the payload prefix.</summary>
        public EncryptedEntity(string purpose, params string[] properties)
            : this(purpose, IsLegacyPlaintext, NotAPayload, properties)
        {
        }

        public Type EntityType => typeof(TEntity);

        public string Purpose => purpose;

        public IReadOnlyList<string> Properties => properties;

        public void Configure(ModelBuilder modelBuilder, IDataProtectionProvider provider)
        {
            var converter = new EncryptedStringConverter(provider, purpose, isLegacyPlaintext);
            foreach (var property in properties)
                modelBuilder.Entity<TEntity>().Property<string>(property).HasConversion(converter);
        }

        public async Task<int> EncryptLegacyPlaintextAsync(AppDbContext stored, AppDbContext db, CancellationToken ct)
        {
            // Startup maintenance of every org: no tenant, the rows are only re-encrypted.
            var ids = await stored.Set<TEntity>()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(StoredInClear())
                .Select(e => EF.Property<Guid>(e, "Id"))
                .ToListAsync(ct);

            foreach (var batch in ids.Chunk(BatchSize))
            {
                var rows = await db.Set<TEntity>()
                    .IgnoreQueryFilters()
                    .Where(e => batch.Contains(EF.Property<Guid>(e, "Id")))
                    .ToListAsync(ct);

                foreach (var row in rows)
                {
                    foreach (var property in properties)
                        db.Entry(row).Property(property).IsModified = true;
                }

                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }

            return ids.Count;
        }

        /// <summary>
        /// At least one column neither empty nor encrypted:
        /// <c>(col IS NOT NULL AND col &lt;&gt; '' AND &lt;storedInClear(col)&gt;) OR …</c>.
        /// </summary>
        private Expression<Func<TEntity, bool>> StoredInClear()
        {
            var entity = Expression.Parameter(typeof(TEntity), "e");
            Expression? body = null;
            foreach (var property in properties)
            {
                var column = Expression.Call(
                    typeof(EF), nameof(EF.Property), [typeof(string)], entity, Expression.Constant(property));
                var inClear = Expression.AndAlso(
                    Expression.AndAlso(
                        Expression.NotEqual(column, Expression.Constant(null, typeof(string))),
                        Expression.NotEqual(column, Expression.Constant(string.Empty))),
                    new ReplaceParameter(storedInClear.Parameters[0], column).Visit(storedInClear.Body));
                body = body is null ? inClear : Expression.OrElse(body, inClear);
            }

            return Expression.Lambda<Func<TEntity, bool>>(body!, entity);
        }
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}

/// <summary>An encrypted column: entity, property and Data Protection purpose (<see cref="EncryptedColumns.All"/>).</summary>
public sealed record EncryptedColumn(Type EntityType, string Property, string Purpose);
