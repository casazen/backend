## Security Issue

**Current Problem**: OTA API keys are stored in plain text in the `OtaIntegrations` table. This is a **critical security vulnerability**.

## User Story

As a **security engineer**, I want **API keys encrypted at rest**, so that **sensitive credentials are protected if the database is compromised**.

## Technical Details

### Solution: Use EF Core Value Converters with Data Protection API

### Files to Create

1. **Casazen.Infrastructure/Data/Encryption/EncryptedStringConverter.cs**
```csharp
public class EncryptedStringConverter : ValueConverter<string, string>
{
    public EncryptedStringConverter(IDataProtectionProvider provider)
        : base(
            plaintext => Encrypt(plaintext, provider),
            ciphertext => Decrypt(ciphertext, provider)
        )
    { }

    private static string Encrypt(string plaintext, IDataProtectionProvider provider)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        var protector = provider.CreateProtector("OtaApiKeys");
        return protector.Protect(plaintext);
    }

    private static string Decrypt(string ciphertext, IDataProtectionProvider provider)
    {
        if (string.IsNullOrEmpty(ciphertext))
            return ciphertext;

        var protector = provider.CreateProtector("OtaApiKeys");
        return protector.Unprotect(ciphertext);
    }
}
```

2. **Update AppDbContext.cs**
```csharp
public class AppDbContext : DbContext
{
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        IDataProtectionProvider dataProtectionProvider)
        : base(options)
    {
        _dataProtectionProvider = dataProtectionProvider;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Encrypt OTA API keys
        modelBuilder.Entity<OtaIntegration>()
            .Property(e => e.ApiKey)
            .HasConversion(new EncryptedStringConverter(_dataProtectionProvider));

        modelBuilder.Entity<OtaIntegration>()
            .Property(e => e.ApiSecret)
            .HasConversion(new EncryptedStringConverter(_dataProtectionProvider));
    }
}
```

3. **Configure Data Protection in Program.cs**
```csharp
// Add Data Protection
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(@"C:\casazen-keys")) // Production: use Azure Key Vault
    .SetApplicationName("CasaZen")
    .SetDefaultKeyLifetime(TimeSpan.FromDays(90));
```

4. **Migration Script**
```csharp
// Casazen.Infrastructure/Migrations/EncryptExistingApiKeys.cs
public partial class EncryptExistingApiKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // NOTE: Existing keys will be automatically encrypted on first read/write
        // due to the value converter. No manual migration needed.

        // However, add a flag to track migration status
        migrationBuilder.AddColumn<bool>(
            name: "IsEncrypted",
            table: "OtaIntegrations",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsEncrypted",
            table: "OtaIntegrations");
    }
}
```

5. **Update OtaIntegration entity**
```csharp
public class OtaIntegration
{
    // ... existing properties ...

    [MaxLength(2000)] // Encrypted strings are longer
    public string ApiKey { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string ApiSecret { get; set; } = string.Empty;

    public bool IsEncrypted { get; set; } = false; // Track encryption status
}
```

6. **One-time migration service** (encrypt existing keys)
```csharp
// Casazen.Infrastructure/Services/ApiKeyMigrationService.cs
public class ApiKeyMigrationService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ApiKeyMigrationService> _logger;

    public async Task EncryptExistingKeysAsync()
    {
        var unencryptedIntegrations = await _context.OtaIntegrations
            .Where(o => !o.IsEncrypted && !string.IsNullOrEmpty(o.ApiKey))
            .ToListAsync();

        foreach (var integration in unencryptedIntegrations)
        {
            // Reading and writing will trigger encryption via value converter
            integration.IsEncrypted = true;
            integration.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation("Encrypted {Count} OTA API keys", unencryptedIntegrations.Count);
    }
}
```

7. **Run migration on startup** (once)
```csharp
// In Program.cs (after app.Build())
using (var scope = app.Services.CreateScope())
{
    var migrationService = scope.ServiceProvider.GetRequiredService<ApiKeyMigrationService>();
    await migrationService.EncryptExistingKeysAsync();
}
```

## Acceptance Criteria

- [ ] EncryptedStringConverter implemented with Data Protection API
- [ ] AppDbContext configured to use converter for ApiKey and ApiSecret
- [ ] Data Protection configured with persistent key storage
- [ ] Migration created for IsEncrypted flag
- [ ] ApiKeyMigrationService encrypts existing keys on first run
- [ ] Unit test: encrypted string in database is not readable
- [ ] Integration test: OTA adapter can still use decrypted keys
- [ ] Keys persist correctly after app restart

## Security Best Practices

### Production Considerations

1. **Key Storage**: Use Azure Key Vault or AWS KMS instead of file system
```csharp
// Production configuration
builder.Services.AddDataProtection()
    .PersistKeysToAzureBlobStorage(/* Azure Blob */)
    .ProtectKeysWithAzureKeyVault(/* Key Vault URI */, /* credentials */);
```

2. **Key Rotation**: Configure automatic key rotation (every 90 days)

3. **Access Control**: Restrict file system permissions on key directory (if using file storage)

## Definition of Done

- [ ] EncryptedStringConverter implemented
- [ ] AppDbContext updated
- [ ] Data Protection configured
- [ ] Migration created and applied
- [ ] ApiKeyMigrationService implemented and run
- [ ] Unit tests pass
- [ ] Integration tests verify encryption/decryption
- [ ] README updated with security notes
- [ ] Code reviewed

## Estimated Effort

**2 days**

## Priority

🔒 **HIGH** - Critical security vulnerability

## Dependencies

None
