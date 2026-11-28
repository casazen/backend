using Casazen.Infrastructure.Services;

namespace Casazen.Tests.Unit.Services;

internal static class MrzSpecimens
{
    public const string Td3Line1 = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<";
    public const string Td3Line2 = "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

    public const string Td1Line1 = "I<UTOD231458907<<<<<<<<<<<<<<<";
    public const string Td1Line2 = "7408122F1204159UTO<<<<<<<<<<<6";
    public const string Td1Line3 = "ERIKSSON<<ANNA<MARIA<<<<<<<<<<";

    public const string Td2Line1 = "I<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<";
    public const string Td2Line2 = "D231458907UTO7408122F1204159<<<<<<<6";

    public static char Check(string value)
    {
        var weights = new[] { 7, 3, 1 };
        var sum = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var number = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => 0,
            };
            sum += number * weights[i % 3];
        }

        return (char)('0' + (sum % 10));
    }

    public static (string Line1, string Line2) Td3(string documentNumber, string birth, string expiry)
    {
        var personal = new string('<', 14);
        var body = documentNumber + Check(documentNumber) + "UTO" + birth + Check(birth) + "F" + expiry + Check(expiry) + personal + "<";
        var composite = body[..10] + body[13..20] + body[21..43];
        return (Td3Line1, body + Check(composite));
    }
}

internal sealed class ScriptedBandLocator(Func<int, MrzLocation> locate) : IMrzBandLocator
{
    public int Calls { get; private set; }

    public List<byte[]> Handed { get; } = [];

    public MrzLocation Locate(GrayBitmap page, CancellationToken cancellationToken)
    {
        Calls++;
        var location = locate(Calls);
        foreach (var line in location.Lines)
        {
            Handed.Add(line.Gray.Pixels);
            Handed.Add(line.Binary.Pixels);
        }

        return location;
    }

    public static MrzLocation Strips(int lineCount, double glareShare = 0, double blurRatio = 0.3)
    {
        var lines = Enumerable.Range(0, lineCount)
            .Select(i => new MrzLine(
                new GrayBitmap(8, 8, Enumerable.Repeat((byte)200, 64).ToArray()),
                new GrayBitmap(8, 8, Enumerable.Repeat((byte)200, 64).ToArray()),
                i * 10,
                (i * 10) + 7,
                0,
                7,
                glareShare,
                blurRatio))
            .ToList();
        return new MrzLocation(lines, [Enumerable.Range(0, lineCount).ToArray()]);
    }
}

internal sealed class ScriptedLineReader(params string[] lines) : IMrzTextReader
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public Task<string?> ReadLineAsync(GrayBitmap strip, CancellationToken cancellationToken)
    {
        if (Failure is not null)
            throw Failure;

        return Task.FromResult<string?>(lines[Calls++ % lines.Length]);
    }
}

internal sealed class ScriptedPageReader(string? text) : IPageTextReader
{
    public Exception? Failure { get; set; }

    public Task<string?> ReadPageAsync(GrayBitmap page, CancellationToken cancellationToken)
    {
        if (Failure is not null)
            throw Failure;

        return Task.FromResult(text);
    }
}
