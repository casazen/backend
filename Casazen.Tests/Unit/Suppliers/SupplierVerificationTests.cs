using System.Collections;
using System.Globalization;
using System.Resources;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-14 (decision D11): "Verificato" = Active profile + Connect enabled + VAT number. The rule is read-only and lives in one
/// place; the activation of the profile is not changed by it.
/// </summary>
public class SupplierVerificationTests
{
    [Theory]
    [InlineData("acct_1", true, true, true)]
    [InlineData("acct_1", true, false, false)] // charges on, payouts not yet: the money could not leave the account
    [InlineData("acct_1", false, true, false)]
    [InlineData("acct_1", false, false, false)]
    [InlineData(null, true, true, false)] // flags without an account (cleared id): never ready
    [InlineData("", true, true, false)]
    [InlineData("   ", true, true, false)]
    public void CanReceivePayments_NeedsALinkedAccountWithChargesAndPayoutsEnabled(
        string? accountId,
        bool chargesEnabled,
        bool payoutsEnabled,
        bool expected)
    {
        Assert.Equal(expected, SupplierVerification.CanReceivePayments(accountId, chargesEnabled, payoutsEnabled));
    }

    [Fact]
    public void IsVerified_ActiveProfileWithPaymentsAndVatNumber_IsTrueAndMissesNothing()
    {
        Assert.True(SupplierVerification.IsVerified(SupplierStatus.Active, canReceivePayments: true, "IT12345678901"));
        Assert.Empty(SupplierVerification.Missing(SupplierStatus.Active, canReceivePayments: true, "IT12345678901"));
    }

    [Theory]
    [InlineData(SupplierStatus.Pending)]
    [InlineData(SupplierStatus.Suspended)]
    public void IsVerified_ProfileNotActive_IsFalseWhateverTheRest(SupplierStatus status)
    {
        Assert.False(SupplierVerification.IsVerified(status, canReceivePayments: true, "IT12345678901"));
        Assert.Equal(
            [SupplierVerification.ProfileNotActive],
            SupplierVerification.Missing(status, canReceivePayments: true, "IT12345678901"));
    }

    [Fact]
    public void IsVerified_PaymentsNotEnabled_IsFalse()
    {
        Assert.False(SupplierVerification.IsVerified(SupplierStatus.Active, canReceivePayments: false, "IT12345678901"));
        Assert.Equal(
            [SupplierVerification.PaymentsNotEnabled],
            SupplierVerification.Missing(SupplierStatus.Active, canReceivePayments: false, "IT12345678901"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsVerified_WithoutVatNumber_IsFalse(string? vatNumber)
    {
        Assert.False(SupplierVerification.IsVerified(SupplierStatus.Active, canReceivePayments: true, vatNumber));
        Assert.Equal(
            [SupplierVerification.VatNumberMissing],
            SupplierVerification.Missing(SupplierStatus.Active, canReceivePayments: true, vatNumber));
    }

    [Fact]
    public void Missing_EverythingMissing_ListsTheThreeConditionsInAStableOrder()
    {
        Assert.Equal(
            [SupplierVerification.ProfileNotActive, SupplierVerification.PaymentsNotEnabled, SupplierVerification.VatNumberMissing],
            SupplierVerification.Missing(SupplierStatus.Pending, canReceivePayments: false, vatNumber: null));
    }

    [Fact]
    public void MessageKeys_TheNotReadyMessage_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.Equal(["SupplierPaymentsNotReady"], SupplierPaymentsErrors.MessageKeys);
        Assert.Equal("supplier_payments_not_ready", SupplierPaymentsErrors.NotReady);
        Assert.All(SupplierPaymentsErrors.MessageKeys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
    }
}
