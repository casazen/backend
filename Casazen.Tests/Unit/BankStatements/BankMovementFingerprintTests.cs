using Casazen.Core.BankStatements;
using Xunit;

namespace Casazen.Tests.Unit.BankStatements;

/// <summary>The identity of a movement (so that importing it twice changes nothing) and the hashes of who paid (so that nothing readable is kept).</summary>
public class BankMovementFingerprintTests
{
    private static readonly Guid Org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherOrg = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static BankMovement Movement(string description = "Canone ottobre", string? payer = "Giulia Verdi", string? iban = null, string? reference = null) =>
        new(new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 4), 900.00m, "EUR", description, payer, iban, reference);

    [Fact]
    public void ForAll_TheSameMovement_HasTheSameFingerprint_ASha256InHex()
    {
        var first = BankMovementFingerprint.ForAll(Org, [Movement()]);
        var again = BankMovementFingerprint.ForAll(Org, [Movement()]);

        Assert.Equal(first, again);
        Assert.Matches("^[0-9a-f]{64}$", first[0]);
    }

    [Fact]
    public void ForAll_TheSameMovementInAnotherOrg_HasAnotherFingerprint()
    {
        Assert.NotEqual(BankMovementFingerprint.ForAll(Org, [Movement()])[0], BankMovementFingerprint.ForAll(OtherOrg, [Movement()])[0]);
    }

    [Fact]
    public void ForAll_TwoIdenticalPaymentsTheSameDay_AreToldApartByTheirOrder()
    {
        var fingerprints = BankMovementFingerprint.ForAll(Org, [Movement(), Movement(), Movement(description: "Altro")]);

        Assert.Equal(3, fingerprints.Distinct().Count());
        // Another file with the same two payments, and others between them, gives the same two values.
        var other = BankMovementFingerprint.ForAll(Org, [Movement(description: "Prima"), Movement(), Movement(), Movement(description: "Altro")]);
        Assert.Equal(fingerprints[0], other[1]);
        Assert.Equal(fingerprints[1], other[2]);
        Assert.Equal(fingerprints[2], other[3]);
    }

    [Fact]
    public void ForAll_CaseSpacesAndAccents_DoNotChangeTheMovement()
    {
        var one = BankMovementFingerprint.ForAll(Org, [Movement(description: "Canone  OTTOBRE  perché", payer: "GIULIA   VERDI")]);
        var other = BankMovementFingerprint.ForAll(Org, [Movement(description: "canone ottobre perche", payer: "Giulia Verdi")]);

        Assert.Equal(one, other);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("valueDate")]
    [InlineData("bookingDate")]
    [InlineData("description")]
    [InlineData("payer")]
    [InlineData("reference")]
    [InlineData("iban")]
    public void ForAll_AnythingTheBankWroteDifferently_IsAnotherMovement(string change)
    {
        var baseline = Movement();
        var changed = change switch
        {
            "amount" => baseline with { Amount = 901.00m },
            "valueDate" => baseline with { ValueDate = new DateOnly(2026, 10, 5) },
            "bookingDate" => baseline with { BookingDate = new DateOnly(2026, 10, 2) },
            "description" => baseline with { Description = "Canone novembre" },
            "payer" => baseline with { CounterpartyName = "Mario Rossi" },
            "reference" => baseline with { BankReference = "R2" },
            _ => baseline with { CounterpartyIban = "DE89370400440532013000" },
        };

        Assert.NotEqual(BankMovementFingerprint.ForAll(Org, [baseline])[0], BankMovementFingerprint.ForAll(Org, [changed])[0]);
    }

    private static readonly byte[] Key = BankPayerHash.DeriveKey("a-long-random-secret-of-the-server")!;
    private static readonly byte[] OtherKey = BankPayerHash.DeriveKey("another-long-random-secret")!;

    [Fact]
    public void PayerName_TheOrderOfTheWordsAndTheAccents_DoNotMatter()
    {
        Assert.Equal(BankPayerHash.Name(Org, "Verdi Giulia", Key), BankPayerHash.Name(Org, "GIULIA VERDI", Key));
        Assert.Equal(BankPayerHash.Name(Org, "Niccolò D'Angelo", Key), BankPayerHash.Name(Org, "ANGELO D NICCOLO", Key));
        Assert.NotEqual(BankPayerHash.Name(Org, "Giulia Verdi", Key), BankPayerHash.Name(Org, "Giulia Rossi", Key));
    }

    [Fact]
    public void PayerHashes_AreOfTheOrg_AndNeverTheValue()
    {
        const string iban = "DE89370400440532013000";

        Assert.NotEqual(BankPayerHash.Iban(Org, iban, Key), BankPayerHash.Iban(OtherOrg, iban, Key));
        Assert.NotEqual(BankPayerHash.Name(Org, "Giulia Verdi", Key), BankPayerHash.Name(OtherOrg, "Giulia Verdi", Key));
        Assert.DoesNotContain(iban, BankPayerHash.Iban(Org, iban, Key)!, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", BankPayerHash.Name(Org, "Giulia Verdi", Key)!);
        Assert.Equal(BankPayerHash.Iban(Org, iban, Key), BankPayerHash.Iban(Org, " de89370400440532013000 ", Key));
    }

    [Fact]
    public void PayerHashes_AreKeyed_SoTheTableAloneCannotBeGuessedAgainst()
    {
        const string iban = "DE89370400440532013000";

        // Whoever has the table and not the key cannot hash a guess: another key, another hash; and it is no plain SHA-256 of anything simple.
        Assert.NotEqual(BankPayerHash.Iban(Org, iban, Key), BankPayerHash.Iban(Org, iban, OtherKey));
        Assert.NotEqual(BankPayerHash.Name(Org, "Giulia Verdi", Key), BankPayerHash.Name(Org, "Giulia Verdi", OtherKey));
        Assert.NotEqual(BankMovementFingerprint.Hash(iban), BankPayerHash.Iban(Org, iban, Key));
        Assert.NotEqual(BankMovementFingerprint.Hash($"cz-bank-payer-iban-v1|{Org:N}|{iban}"), BankPayerHash.Iban(Org, iban, Key));
    }

    [Fact]
    public void PayerHashes_WithoutAKey_KeepNothing()
    {
        Assert.Null(BankPayerHash.Iban(Org, "DE89370400440532013000", null));
        Assert.Null(BankPayerHash.Name(Org, "Giulia Verdi", null));
        Assert.Null(BankPayerHash.DeriveKey(null));
        Assert.Null(BankPayerHash.DeriveKey("   "));
        Assert.Equal(32, BankPayerHash.DeriveKey("x")!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("X")]
    [InlineData("Li")]
    public void PayerName_ThereIsNothingToRecognizeInABlankOrTinyName(string? value)
    {
        Assert.Null(BankPayerHash.Name(Org, value, Key));
    }

    [Fact]
    public void PayerIban_ABlankValue_HasNoHash()
    {
        Assert.Null(BankPayerHash.Iban(Org, null, Key));
        Assert.Null(BankPayerHash.Iban(Org, "  ", Key));
    }
}
