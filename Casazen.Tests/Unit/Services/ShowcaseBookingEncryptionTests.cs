using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10: the personal data of a customer who books from a supplier's showcase are encrypted at rest — the booking that waits for
/// the e-mail check keeps them in one encrypted payload, then they are split between the customer (name, e-mail, phone) and the
/// request (street address, floor, access notes). The EF in-memory provider keeps the values it is given, so what is stored is
/// proved on PostgreSQL (<c>ShowcaseBookingPostgresTests</c>, <c>FieldEncryptionPostgresTests</c> style); here the model carries
/// the right converter on exactly the right columns, and the whole flow still works — and the supplier still reads the place and
/// the contacts of a request it took — with the encryption configured.
/// </summary>
public class ShowcaseBookingEncryptionTests
{
    private static readonly (Type Entity, string Property, string Purpose)[] EncryptedColumnsOfSp10 =
    [
        (typeof(ServiceCustomer), nameof(ServiceCustomer.FullName), EncryptedColumns.ServiceCustomerPurpose),
        (typeof(ServiceCustomer), nameof(ServiceCustomer.Email), EncryptedColumns.ServiceCustomerPurpose),
        (typeof(ServiceCustomer), nameof(ServiceCustomer.Phone), EncryptedColumns.ServiceCustomerPurpose),
        (typeof(ShowcaseBookingHold), nameof(ShowcaseBookingHold.Payload), EncryptedColumns.ServiceCustomerPurpose),
        (typeof(ServiceRequest), nameof(ServiceRequest.LocationAddress), EncryptedColumns.ServiceRequestLocationPurpose),
        (typeof(ServiceRequest), nameof(ServiceRequest.LocationFloor), EncryptedColumns.ServiceRequestLocationPurpose),
        (typeof(ServiceRequest), nameof(ServiceRequest.LocationAccessNotes), EncryptedColumns.ServiceRequestLocationPurpose),
    ];

    private static AppDbContext NewContext(IDataProtectionProvider? provider) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenantContext: null, provider);

    [Fact]
    public void TheNewPurposes_AreTwo_AndDifferent()
    {
        Assert.Equal("Casazen.ServiceCustomer", EncryptedColumns.ServiceCustomerPurpose);
        Assert.Equal("Casazen.ServiceRequest.Location", EncryptedColumns.ServiceRequestLocationPurpose);
        Assert.NotEqual(EncryptedColumns.ServiceCustomerPurpose, EncryptedColumns.ServiceRequestLocationPurpose);
        // The purposes of the guests of the hosts are not reused: a key for one data set opens nothing of the other.
        Assert.DoesNotContain(EncryptedColumns.GuestDocumentPurpose, new[] { EncryptedColumns.ServiceCustomerPurpose, EncryptedColumns.ServiceRequestLocationPurpose });
    }

    [Fact]
    public void EveryColumnOfTheBooking_HasTheConverterOfItsPurpose_AndItsValuesComeBackThroughIt()
    {
        var provider = new EphemeralDataProtectionProvider();
        using var db = NewContext(provider);

        foreach (var (entity, property, purpose) in EncryptedColumnsOfSp10)
        {
            var column = db.Model.FindEntityType(entity)!.FindProperty(property)!;
            var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());

            var stored = (string)converter.ConvertToProvider("Mario Rossi, Via Segretissima 7")!;

            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
            Assert.DoesNotContain("Rossi", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("Segretissima", stored, StringComparison.Ordinal);
            Assert.Equal("Mario Rossi, Via Segretissima 7", provider.CreateProtector(purpose).Unprotect(stored));
            Assert.Equal("Mario Rossi, Via Segretissima 7", converter.ConvertFromProvider(stored));
            // Only the purpose of the column opens it.
            var other = purpose == EncryptedColumns.ServiceCustomerPurpose
                ? EncryptedColumns.ServiceRequestLocationPurpose
                : EncryptedColumns.ServiceCustomerPurpose;
            Assert.ThrowsAny<CryptographicException>(() => provider.CreateProtector(other).Unprotect(stored));
        }
    }

    [Fact]
    public void WhatTheSupplierSeesBeforeTheTake_AndWhatFindsACustomer_IsNotEncrypted()
    {
        using var db = NewContext(new EphemeralDataProtectionProvider());
        var clear = new (Type Entity, string Property)[]
        {
            (typeof(ServiceRequest), nameof(ServiceRequest.LocationCity)),
            (typeof(ServiceRequest), nameof(ServiceRequest.LocationPostalCode)),
            (typeof(ServiceRequest), nameof(ServiceRequest.LocationComuneIstat)),
            (typeof(ServiceRequest), nameof(ServiceRequest.PublicCode)),
            (typeof(ServiceCustomer), nameof(ServiceCustomer.EmailHash)),
            (typeof(ServiceCustomer), nameof(ServiceCustomer.Locale)),
            (typeof(ShowcaseBookingHold), nameof(ShowcaseBookingHold.TokenHash)),
            (typeof(ShowcaseBookingHold), nameof(ShowcaseBookingHold.EmailHash)),
            (typeof(ShowcaseBookingHold), nameof(ShowcaseBookingHold.PublicCode)),
        };

        Assert.All(clear, column =>
            Assert.Null(db.Model.FindEntityType(column.Entity)!.FindProperty(column.Property)!.GetValueConverter()));
    }

    [Fact]
    public void WithoutAProvider_TheModelHasNoConverter_SoNothingIsEverStoredInClearByMistakeInProduction()
    {
        using var encrypted = NewContext(new EphemeralDataProtectionProvider());
        using var plain = NewContext(null);

        Assert.True(EncryptedColumns.IsConfigured(encrypted));
        Assert.False(EncryptedColumns.IsConfigured(plain));
        Assert.All(EncryptedColumnsOfSp10, column =>
            Assert.Null(plain.Model.FindEntityType(column.Entity)!.FindProperty(column.Property)!.GetValueConverter()));
    }

    [Fact]
    public async Task TheWholeBooking_WorksWithTheEncryptionConfigured()
    {
        var provider = new EphemeralDataProtectionProvider();
        using var s = await ServiceRequestScenario.CreateAsync(dataProtection: provider);
        await s.EnableBookingAsync();
        Assert.True(EncryptedColumns.IsConfigured(s.Db));

        var (booked, confirmation) = await s.BookedAsync();

        var hold = Assert.Single(await s.HoldsAsync());
        Assert.Null(hold.Payload);
        Assert.Equal(confirmation.PublicCode, hold.PublicCode);
        var customer = Assert.Single(await s.CustomersAsync());
        Assert.Equal(ShowcaseScenario.CustomerName, customer.FullName);
        Assert.Equal(ShowcaseScenario.CustomerEmail, customer.Email);
        Assert.Equal("+393331234567", customer.Phone);
        var request = await s.ReadAsync(booked.Id);
        Assert.Equal(ShowcaseScenario.Address, request.LocationAddress);
        Assert.Equal(ShowcaseScenario.Floor, request.LocationFloor);
        Assert.Equal(ShowcaseScenario.AccessNotes, request.LocationAccessNotes);
    }

    [Fact]
    public async Task TheSuppliersConsole_ReadsThePlaceAndTheContacts_OnlyAfterTheTake_WithTheEncryptionConfigured()
    {
        var provider = new EphemeralDataProtectionProvider();
        using var s = await ServiceRequestScenario.CreateAsync(dataProtection: provider);
        await s.EnableBookingAsync();
        var (booked, _) = await s.BookedAsync();

        var before = await s.Reader.GetAsync(booked.Id, s.SupplierOrgId);
        await s.Service.TakeAsync(booked.Id, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId);
        var after = await s.Reader.GetAsync(booked.Id, s.SupplierOrgId);

        Assert.Null(before!.Location.Address);
        Assert.Null(before.HostContact);
        Assert.Equal(ShowcaseScenario.Address, after!.Location.Address);
        Assert.Equal(ShowcaseScenario.Floor, after.Location.Floor);
        Assert.Equal(ShowcaseScenario.AccessNotes, after.Location.AccessNotes);
        Assert.Equal(new SupplierJobHostContact("Mario Rossi", ShowcaseScenario.CustomerEmail, "+393331234567"), after.HostContact);
    }
}
