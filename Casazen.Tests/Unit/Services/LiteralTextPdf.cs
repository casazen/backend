using System.Text;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Minimal uncompressed PDF whose text is written as literal strings, as many third-party generators do: input for
/// the tests of the APE upload inspector. CasaZen's own PDFs come from <c>IPdfDocumentRenderer</c> (LT-09).
/// </summary>
internal static class LiteralTextPdf
{
    public static byte[] Build(string title, string body)
    {
        var lines = $"{title}\n\n{body}".Split('\n').Select(l => $"({Escape(l)}) Tj T*");
        var content = $"BT /F1 11 Tf 50 780 Td 14 TL {string.Join(' ', lines)} ET";

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string s)
        {
            offsets.Add(sb.Length);
            sb.Append(s);
        }

        Obj("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        Obj("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        Obj("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj\n");
        Obj($"4 0 obj << /Length {content.Length} >> stream\n{content}\nendstream endobj\n");
        Obj("5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n");

        var xref = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append($"{offset:D10} 00000 n \n");
        sb.Append($"trailer << /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ");
}
