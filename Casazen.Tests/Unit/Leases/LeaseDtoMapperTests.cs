using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.DTOs.Leases;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>LT-11 (A7-17): the lease API returns DTOs without clear personal data nor internal fields.</summary>
public class LeaseDtoMapperTests
{
    private const string LandlordCf = "RSSMRA80A01H501Z";
    private const string TenantCf = "VRDGLI85B02F205X";
    private const string LandlordEmail = "mario.rossi@example.com";
    private const string TenantEmail = "giulia.verdi@example.com";

    // Same enum handling as the API (Program.cs).
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>JSON names that must never appear in a lease response.</summary>
    private static readonly string[] ForbiddenFields =
    [
        "ownerId", "orgId", "safetyChecklistJson", "fiscalCode", "contactEmail", "citizenship", "payload",
        "signedPdfStoragePath", "externalSigningSessionId", "externalRegistrationId", "receiptStoragePath",
        "dataRetentionUntil", "erasureRequested", "leaseContract",
    ];

    [Fact]
    public void ToDetail_LeaseWithParties_MasksFiscalCodeAndEmail()
    {
        var detail = LeaseDtoMapper.ToDetail(BuildLease());

        var landlord = detail.Parties.Single(p => p.Role == PartyRole.Landlord);
        Assert.Equal("Mario", landlord.FirstName);
        Assert.Equal("************501Z", landlord.FiscalCodeMasked);
        Assert.Equal("m***@example.com", landlord.ContactEmailMasked);
        var tenant = detail.Parties.Single(p => p.Role == PartyRole.Tenant);
        Assert.True(tenant.IsExtraEU);
        Assert.True(detail.HasExtraEUTenant);
        Assert.True(detail.HasSignedPdf);
    }

    [Fact]
    public void ToDetail_SerializedAsTheApiDoes_ContainsNoClearPersonalDataNorInternalFields()
    {
        var json = JsonSerializer.Serialize(LeaseDtoMapper.ToDetail(BuildLease()), ApiJson);

        foreach (var secret in new[] { LandlordCf, TenantCf, LandlordEmail, TenantEmail, "auth0|owner", "/signed/", "provider-123", "receipts/", "{\"extinguisher\":true}", "US" })
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        AssertNoForbiddenFields(json);
        Assert.Contains("\"eventType\":\"PartySignedDocument\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToRegistration_Registered_OmitsProviderIdAndReceiptPath()
    {
        var json = JsonSerializer.Serialize(LeaseDtoMapper.ToRegistration(BuildLease().Registration!), ApiJson);

        Assert.Contains("\"registrationCode\":\"RLI-2026-1\"", json, StringComparison.Ordinal);
        AssertNoForbiddenFields(json);
        Assert.DoesNotContain("provider-123", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(LeaseSummaryDto))]
    [InlineData(typeof(LeaseDetailDto))]
    [InlineData(typeof(LeasePartyDto))]
    [InlineData(typeof(LeaseEventDto))]
    [InlineData(typeof(LeaseRegistrationDto))]
    [InlineData(typeof(LeasePropertyDto))]
    public void LeaseDtoTypes_Properties_ExposeNoEntityNorPersonalOrInternalField(Type dtoType)
    {
        foreach (var property in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            Assert.DoesNotContain(jsonName, ForbiddenFields);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var elementType = type.IsGenericType ? type.GetGenericArguments()[0] : type;
            Assert.NotEqual(typeof(Property).Namespace, elementType.Namespace);
        }
    }

    [Fact]
    public void LeaseSummaryDto_HasNoPartyDetails()
    {
        var names = typeof(LeaseSummaryDto).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains(nameof(LeaseSummaryDto.PartyCount), names);
        Assert.DoesNotContain(names, n => n.Contains("Part", StringComparison.Ordinal) && n != nameof(LeaseSummaryDto.PartyCount));
    }

    [Theory]
    [InlineData("RSSMRA80A01H501Z", "************501Z")]
    [InlineData(" 12345678901 ", "*******8901")]
    [InlineData("ABC", "****")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void MaskFiscalCode_Value_KeepsOnlyLastFourCharacters(string? value, string expected)
    {
        Assert.Equal(expected, PersonalDataMasking.MaskFiscalCode(value));
    }

    [Theory]
    [InlineData("mario.rossi@example.it", "m***@example.it")]
    [InlineData("not-an-email", "***")]
    [InlineData("@example.it", "***")]
    [InlineData(null, "")]
    public void MaskEmail_Value_KeepsFirstCharacterAndDomain(string? value, string expected)
    {
        Assert.Equal(expected, PersonalDataMasking.MaskEmail(value));
    }

    private static void AssertNoForbiddenFields(string json)
    {
        foreach (var field in ForbiddenFields)
            Assert.DoesNotContain($"\"{field}\":", json, StringComparison.Ordinal);
    }

    private static LeaseContract BuildLease()
    {
        var leaseId = Guid.NewGuid();
        var lease = new LeaseContract
        {
            Id = leaseId,
            PropertyId = Guid.NewGuid(),
            OrgId = Guid.NewGuid(),
            Status = LeaseStatus.Registered,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            RegistrationDeadline = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            DataRetentionUntil = new DateTime(2036, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            ExternalSigningSessionId = "session-1",
            SignedPdfStoragePath = "/signed/lease.pdf",
            Property = new Property
            {
                Id = Guid.NewGuid(),
                Name = "Casa Roma",
                City = "Roma",
                OwnerId = "auth0|owner",
                Address = "Via Roma 1",
                SafetyChecklistJson = "{\"extinguisher\":true}",
            },
            Parties =
            [
                new Party
                {
                    Role = PartyRole.Tenant,
                    FirstName = "Giulia",
                    LastName = "Verdi",
                    FiscalCode = TenantCf,
                    Citizenship = "US",
                    ContactEmail = TenantEmail,
                    IsExtraEU = true,
                },
                new Party
                {
                    Role = PartyRole.Landlord,
                    FirstName = "Mario",
                    LastName = "Rossi",
                    FiscalCode = LandlordCf,
                    Citizenship = "IT",
                    ContactEmail = LandlordEmail,
                },
            ],
            Registration = new LeaseRegistration
            {
                LeaseContractId = leaseId,
                Status = RegistrationStatus.Registered,
                ExternalRegistrationId = "provider-123",
                RegistrationCode = "RLI-2026-1",
                ReceiptStoragePath = "receipts/lease.pdf",
                SubmittedAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc),
                ConfirmedAt = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc),
            },
            Events =
            [
                new LeaseEvent { EventType = LeaseEventType.PartySignedDocument, Payload = TenantEmail },
                new LeaseEvent { EventType = LeaseEventType.RegistrationAuthorized, Payload = "2026-08-rli-delega-bozza" },
            ],
        };
        foreach (var party in lease.Parties)
            party.LeaseContract = lease;
        return lease;
    }
}
