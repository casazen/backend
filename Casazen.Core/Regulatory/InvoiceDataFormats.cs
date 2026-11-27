using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Regulatory;

/// <summary>One thing wrong in the invoice data of a booking: a stable code (<see cref="InvoiceDataErrorCodes"/>) and the field it is about.</summary>
/// <param name="Code">One of the codes of <see cref="InvoiceDataErrorCodes"/>.</param>
/// <param name="Field">The name of the field in the API (<see cref="InvoiceDataFields"/>), camel case.</param>
public sealed record InvoiceDataFailure(string Code, string Field);

/// <summary>The names of the fields of the invoice data as the API says them (camel case), in each error that names a field.</summary>
public static class InvoiceDataFields
{
    public const string RecipientKind = "recipientKind";
    public const string Name = "name";
    public const string FiscalCode = "fiscalCode";
    public const string VatNumber = "vatNumber";
    public const string AddressStreet = "addressStreet";
    public const string AddressCivic = "addressCivic";
    public const string PostalCode = "postalCode";
    public const string City = "city";
    public const string Province = "province";
    public const string CountryCode = "countryCode";
    public const string SdiRecipientCode = "sdiRecipientCode";
    public const string Pec = "pec";
}

/// <summary>
/// The stable codes of the errors of the invoice data of a booking (E06/F02/US01/T02), each with its message in <c>SharedResources</c> (Italian and
/// English). Every response that carries one names the field it is about (<see cref="InvoiceDataFields"/>).
/// </summary>
public static class InvoiceDataErrorCodes
{
    /// <summary>The fiscal code is not valid for its country (Italy: <see cref="ItalianFiscalCode"/>).</summary>
    public const string FiscalCodeInvalid = "invoice_data_fiscal_code_invalid";

    /// <summary>The VAT number is not valid: Italian with its check digit, another country by its shape.</summary>
    public const string VatNumberInvalid = "invoice_data_vat_number_invalid";

    /// <summary>The code of the recipient on the SDI is not 7 upper case letters or digits.</summary>
    public const string RecipientCodeInvalid = "invoice_data_recipient_code_invalid";

    /// <summary>The PEC is not an e-mail address.</summary>
    public const string PecInvalid = "invoice_data_pec_invalid";

    /// <summary>The recipient is not identified: a company needs a VAT number or a fiscal code, a private person in Italy a fiscal code.</summary>
    public const string RecipientRequired = "invoice_data_recipient_required";

    /// <summary>Any other field is too long or has a shape the field cannot have (province, country, postal code, kind of recipient).</summary>
    public const string FieldInvalid = "invoice_data_field_invalid";

    /// <summary>The key in <c>SharedResources</c> of the message of <paramref name="code"/>.</summary>
    public static string MessageKeyOf(string code) => code switch
    {
        FiscalCodeInvalid => "InvoiceDataFiscalCodeInvalid",
        VatNumberInvalid => "InvoiceDataVatNumberInvalid",
        RecipientCodeInvalid => "InvoiceDataRecipientCodeInvalid",
        PecInvalid => "InvoiceDataPecInvalid",
        RecipientRequired => "InvoiceDataRecipientRequired",
        FieldInvalid => "InvoiceDataFieldInvalid",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a code of the invoice data."),
    };

    /// <summary>
    /// The 422 for <paramref name="failures"/> (at least one): the code and the message of the first, its field as <c>field</c>, and every failure as
    /// <c>errors</c> (<c>[{ field, code }]</c>), so a form can mark each field at once. Ids and codes only, never a value the person typed.
    /// </summary>
    public static DomainRuleException ToException(IReadOnlyList<InvoiceDataFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0)
            throw new ArgumentException("There is at least one failure to turn into an error.", nameof(failures));

        var first = failures[0];
        return new DomainRuleException(first.Code, MessageKeyOf(first.Code))
        {
            Extensions = new Dictionary<string, object?>
            {
                ["field"] = first.Field,
                ["errors"] = failures.Select(f => new { field = f.Field, code = f.Code }).ToList(),
            },
        };
    }
}

/// <summary>
/// The formats of the invoice data of a booking (FIS-01, NF-81, E06/F02/US01/T02): the one place that knows how a VAT number, a recipient code and a
/// PEC are written, how the text of each field is normalized before it is stored, and the one rule table of the identification of the recipient.
/// The fiscal code is never checked here: <see cref="ItalianFiscalCode"/> is the single place that knows it.
/// </summary>
public static class InvoiceDataFormats
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>A VAT number of another country: its two letters (the EU prefix, <c>EL</c> for Greece) and 2 to 12 letters or digits. By shape only.</summary>
    private static readonly Regex ForeignVatNumber = new("^[A-Z]{2}[A-Z0-9]{2,12}$", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex RecipientCode = new("^[A-Z0-9]{7}$", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex EmailAddress = new(
        @"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>
    /// True for a valid VAT number: an Italian one is 11 digits with their check digit (the same as the numeric fiscal code), with or without the
    /// <c>IT</c> prefix; the number of another country, written with its prefix, is accepted by its shape only. Spaces, dots, dashes and slashes are
    /// ignored and the case does not matter.
    /// </summary>
    public static bool IsValidVatNumber(string? vatNumber)
    {
        var vat = Compact(vatNumber);
        if (vat.Length == 0)
            return false;

        if (vat.StartsWith("IT", StringComparison.Ordinal))
            return IsValidItalianVat(vat[2..]);

        if (vat.All(char.IsAsciiDigit))
            return IsValidItalianVat(vat);

        return ForeignVatNumber.IsMatch(vat);
    }

    /// <summary>True for exactly 7 upper case letters or digits: the code of the recipient on the SDI. <see cref="NormalizeRecipientCode"/> raises the case first.</summary>
    public static bool IsValidRecipientCode(string? code) => code is not null && RecipientCode.IsMatch(code);

    /// <summary>True for an e-mail address of at most <see cref="BookingInvoiceData.PecMaxLength"/> characters: one at sign, a name before it and a domain with a dot after it.</summary>
    public static bool IsValidPec(string? pec)
    {
        if (string.IsNullOrEmpty(pec) || pec.Length > BookingInvoiceData.PecMaxLength || !EmailAddress.IsMatch(pec))
            return false;

        var local = pec[..pec.IndexOf('@')];
        return !local.StartsWith('.') && !local.EndsWith('.') && !local.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// The one rule table of the identification of the recipient: the name of the field that is missing, or <c>null</c> when the recipient is
    /// identified. A recipient outside Italy needs neither a fiscal code nor a VAT number; a company in Italy a VAT number or a fiscal code; a
    /// private person in Italy a fiscal code.
    /// </summary>
    public static string? MissingIdentification(InvoiceRecipientKind kind, string? countryCode, string? fiscalCode, string? vatNumber)
    {
        if (!IsItaly(countryCode))
            return null;

        return kind switch
        {
            InvoiceRecipientKind.Company => string.IsNullOrWhiteSpace(vatNumber) && string.IsNullOrWhiteSpace(fiscalCode)
                ? InvoiceDataFields.VatNumber
                : null,
            _ => string.IsNullOrWhiteSpace(fiscalCode) ? InvoiceDataFields.FiscalCode : null,
        };
    }

    /// <summary>
    /// What is wrong with <paramref name="state"/>, read as the whole invoice data of a booking (a field that is <c>null</c> or empty is not
    /// there): each failure with its field, in the order fiscal code, VAT number, recipient code, PEC, the other fields, and the identification of the
    /// recipient when <paramref name="requireIdentification"/> (the data a person typed, not the partial data of an import). Empty when it is valid.
    /// The text is expected already normalized (<see cref="Normalize"/>).
    /// </summary>
    public static IReadOnlyList<InvoiceDataFailure> Validate(SaveInvoiceDataCommand state, bool requireIdentification)
    {
        ArgumentNullException.ThrowIfNull(state);

        var failures = new List<InvoiceDataFailure>();
        var country = string.IsNullOrEmpty(state.CountryCode) ? BookingInvoiceData.DefaultCountryCode : state.CountryCode;
        var italy = IsItaly(country);
        var kind = state.RecipientKind ?? InvoiceRecipientKind.Guest;

        if (state.RecipientKind is { } given && !Enum.IsDefined(given))
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, InvoiceDataFields.RecipientKind));

        if (!string.IsNullOrEmpty(state.FiscalCode)
            && (state.FiscalCode.Length > BookingInvoiceData.FiscalCodeMaxLength || (italy && !ItalianFiscalCode.IsValid(state.FiscalCode))))
        {
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.FiscalCodeInvalid, InvoiceDataFields.FiscalCode));
        }

        if (!string.IsNullOrEmpty(state.VatNumber)
            && (state.VatNumber.Length > BookingInvoiceData.VatNumberMaxLength || !IsValidVatNumber(state.VatNumber)))
        {
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.VatNumberInvalid, InvoiceDataFields.VatNumber));
        }

        if (!string.IsNullOrEmpty(state.SdiRecipientCode) && !IsValidRecipientCode(state.SdiRecipientCode))
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.RecipientCodeInvalid, InvoiceDataFields.SdiRecipientCode));

        if (!string.IsNullOrEmpty(state.Pec) && !IsValidPec(state.Pec))
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.PecInvalid, InvoiceDataFields.Pec));

        TooLong(failures, state.Name, BookingInvoiceData.NameMaxLength, InvoiceDataFields.Name);
        TooLong(failures, state.AddressStreet, BookingInvoiceData.AddressStreetMaxLength, InvoiceDataFields.AddressStreet);
        TooLong(failures, state.AddressCivic, BookingInvoiceData.AddressCivicMaxLength, InvoiceDataFields.AddressCivic);
        TooLong(failures, state.City, BookingInvoiceData.CityMaxLength, InvoiceDataFields.City);

        if (!string.IsNullOrEmpty(state.PostalCode)
            && (state.PostalCode.Length > BookingInvoiceData.PostalCodeMaxLength || (italy && !IsItalianPostalCode(state.PostalCode))))
        {
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, InvoiceDataFields.PostalCode));
        }

        if (!string.IsNullOrEmpty(state.Province) && !IsTwoLetters(state.Province))
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, InvoiceDataFields.Province));

        if (!IsTwoLetters(country))
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, InvoiceDataFields.CountryCode));

        if (requireIdentification && failures.Count == 0 && MissingIdentification(kind, country, state.FiscalCode, state.VatNumber) is { } missing)
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.RecipientRequired, missing));

        return failures;
    }

    /// <summary>
    /// <paramref name="command"/> with the text of each field written the way it is stored: a field that is not sent (<c>null</c>) stays so; one that
    /// is sent has its spaces trimmed and collapsed, or is empty (it clears the field); the fiscal code is upper case without spaces, the VAT number
    /// has its country prefix (the one of <paramref name="effectiveCountry"/> when the person wrote none), the country, the province and the code of
    /// the recipient are upper case, the PEC lower case.
    /// </summary>
    /// <param name="command">The data a source sent.</param>
    /// <param name="effectiveCountry">The country of the recipient once the command is applied: the one it sends, else the one stored.</param>
    public static SaveInvoiceDataCommand Normalize(SaveInvoiceDataCommand command, string effectiveCountry)
    {
        ArgumentNullException.ThrowIfNull(command);

        return command with
        {
            Name = Text(command.Name),
            FiscalCode = command.FiscalCode is null ? null : ItalianFiscalCode.Normalize(command.FiscalCode),
            VatNumber = command.VatNumber is null ? null : NormalizeVatNumber(command.VatNumber, effectiveCountry),
            AddressStreet = Text(command.AddressStreet),
            AddressCivic = Text(command.AddressCivic),
            PostalCode = command.PostalCode is null ? null : IsItaly(effectiveCountry) ? Compact(command.PostalCode) : Text(command.PostalCode)?.ToUpperInvariant(),
            City = Text(command.City),
            Province = command.Province is null ? null : Compact(command.Province),
            CountryCode = command.CountryCode is null ? null : Compact(command.CountryCode),
            SdiRecipientCode = command.SdiRecipientCode is null ? null : NormalizeRecipientCode(command.SdiRecipientCode),
            Pec = command.Pec is null ? null : command.Pec.Trim().ToLowerInvariant(),
        };
    }

    /// <summary>The country of the recipient once <paramref name="command"/> is applied to a row whose country is <paramref name="storedCountry"/>.</summary>
    public static string EffectiveCountry(SaveInvoiceDataCommand command, string? storedCountry)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sent = command.CountryCode is null ? string.Empty : Compact(command.CountryCode);
        if (sent.Length > 0)
            return sent;

        return string.IsNullOrEmpty(storedCountry) ? BookingInvoiceData.DefaultCountryCode : storedCountry;
    }

    /// <summary>
    /// The VAT number written with its country prefix, upper case and without separators: <c>IT12345678903</c> for <c>12345678903</c> in Italy, the
    /// prefix of <paramref name="countryCode"/> (<c>EL</c> for Greece) for a number that starts with a digit in another country; a number that
    /// starts with letters keeps them.
    /// </summary>
    public static string NormalizeVatNumber(string? vatNumber, string? countryCode)
    {
        var vat = Compact(vatNumber);
        if (vat.Length == 0 || !char.IsAsciiDigit(vat[0]))
            return vat;

        var country = string.IsNullOrWhiteSpace(countryCode) ? BookingInvoiceData.DefaultCountryCode : Compact(countryCode);
        return (country == "GR" ? "EL" : country) + vat;
    }

    /// <summary>The code of the recipient trimmed and in upper case.</summary>
    public static string NormalizeRecipientCode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// Reads the kind of recipient a client wrote: <c>Guest</c> or <c>Company</c>, in any case. Nothing written is no kind (<c>true</c> and
    /// <paramref name="kind"/> <c>null</c>); anything else, a number included, is <c>false</c>: the caller answers 422 <c>invoice_data_field_invalid</c>
    /// on <see cref="InvoiceDataFields.RecipientKind"/>.
    /// </summary>
    public static bool TryParseRecipientKind(string? text, out InvoiceRecipientKind? kind)
    {
        kind = null;
        var name = text?.Trim();
        if (string.IsNullOrEmpty(name))
            return true;

        if (!Enum.TryParse<InvoiceRecipientKind>(name, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || name.All(char.IsAsciiDigit))
            return false;

        kind = parsed;
        return true;
    }

    private static bool IsItaly(string? countryCode) =>
        string.Equals(string.IsNullOrWhiteSpace(countryCode) ? BookingInvoiceData.DefaultCountryCode : countryCode.Trim(), "IT", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidItalianVat(string digits) =>
        digits.Length == 11 && digits.All(char.IsAsciiDigit) && ItalianFiscalCode.IsValidNumericCode(digits);

    private static bool IsItalianPostalCode(string postalCode) => postalCode.Length == 5 && postalCode.All(char.IsAsciiDigit);

    private static bool IsTwoLetters(string value) => value.Length == 2 && value.All(char.IsAsciiLetter);

    private static void TooLong(List<InvoiceDataFailure> failures, string? value, int maxLength, string field)
    {
        if (value is not null && value.Length > maxLength)
            failures.Add(new InvoiceDataFailure(InvoiceDataErrorCodes.FieldInvalid, field));
    }

    /// <summary>Trimmed, with every run of white space collapsed into one space; <c>null</c> stays <c>null</c>.</summary>
    private static string? Text(string? value)
    {
        if (value is null)
            return null;

        var text = new StringBuilder(value.Length);
        var space = false;
        foreach (var c in value.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
                text.Append(' ');
            space = false;
            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>Upper case with no white space, dot, dash or slash.</summary>
    private static string Compact(string? value)
    {
        var text = new StringBuilder((value ?? string.Empty).Length);
        foreach (var c in value ?? string.Empty)
        {
            if (char.IsWhiteSpace(c) || c is '.' or '/' or '-')
                continue;

            text.Append(char.ToUpperInvariant(c));
        }

        return text.ToString();
    }
}
