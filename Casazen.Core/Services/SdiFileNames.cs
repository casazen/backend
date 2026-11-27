using System.Text.RegularExpressions;

namespace Casazen.Core.Services;

/// <summary>
/// The name of the file of an electronic invoice for the SDI (E17/F01/US02/T01): <c>{country}{transmitter id}_{progressive}.xml</c>, as the technical specifications of
/// the SDI give it (the country code and the fiscal id of the transmitter, an underscore, a progressive of at most five letters and digits, the extension). The SDI refuses
/// a name that is not of this form (code 00001) and a name it already received (00002), so the progressive of a document is stable (the same document, the same name) and
/// tells two documents apart.
/// </summary>
/// <remarks>
/// The provider of the channel may name the file itself (it is the one that holds the accredited channel and its progressives): the adapter says whether it uses this name
/// (<c>docs/runbooks/fiscal-documents.md</c>). The progressive is five characters of base 36 taken from the id of the document: 60 million values, so two documents of one
/// transmitter share one only by chance.
/// </remarks>
public static partial class SdiFileNames
{
    /// <summary>The length of the progressive.</summary>
    public const int ProgressiveLength = 5;

    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The progressive of <paramref name="documentId"/>: five characters of <c>0-9A-Z</c>, always the same for the same document.</summary>
    public static string Progressive(Guid documentId)
    {
        Span<byte> bytes = stackalloc byte[16];
        documentId.TryWriteBytes(bytes);
        var value = BitConverter.ToUInt64(bytes[8..]);

        var characters = new char[ProgressiveLength];
        for (var index = ProgressiveLength - 1; index >= 0; index--)
        {
            characters[index] = Alphabet[(int)(value % (ulong)Alphabet.Length)];
            value /= (ulong)Alphabet.Length;
        }

        return new string(characters);
    }

    /// <summary>The name of the file of <paramref name="documentId"/> sent by the transmitter <paramref name="transmitterId"/> of <paramref name="countryCode"/>.</summary>
    public static string Of(string countryCode, string transmitterId, Guid documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(countryCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(transmitterId);

        var name = $"{countryCode.Trim().ToUpperInvariant()}{transmitterId.Trim().ToUpperInvariant()}_{Progressive(documentId)}.xml";
        return IsValid(name) ? name : throw new ArgumentException("The transmitter does not make a valid name of file.", nameof(transmitterId));
    }

    /// <summary>True when <paramref name="fileName"/> has the form the SDI accepts.</summary>
    public static bool IsValid(string? fileName) => fileName is not null && Pattern().IsMatch(fileName);

    [GeneratedRegex(@"^[A-Z]{2}[A-Za-z0-9]{1,28}_[A-Za-z0-9]{1,5}\.xml$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
