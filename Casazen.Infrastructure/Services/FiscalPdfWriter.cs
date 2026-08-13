using System.Text;

namespace Casazen.Infrastructure.Services;

/// <summary>Minimal PDF 1.4 writer (Helvetica, WinAnsi). Not a placeholder byte array.</summary>
internal static class FiscalPdfWriter
{
    public static byte[] Write(string title, string body)
    {
        var text = Sanitize($"{title}\n\n{body}");
        var content = $"BT /F1 11 Tf 50 780 Td 14 TL ({Escape(text)}) Tj ET";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string s)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(s);
        }

        Obj("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        Obj("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        Obj("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj\n");
        Obj($"4 0 obj << /Length {contentBytes.Length} >> stream\n{content}\nendstream endobj\n");
        Obj("5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n");

        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append($"{off:D10} 00000 n \n");
        sb.Append($"trailer << /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Sanitize(string s)
    {
        var folded = s
            .Replace('à', 'a').Replace('è', 'e').Replace('é', 'e')
            .Replace('ì', 'i').Replace('ò', 'o').Replace('ù', 'u')
            .Replace('À', 'A').Replace('È', 'E').Replace('É', 'E')
            .Replace('—', '-').Replace('«', '"').Replace('»', '"');
        return folded.Length > 1800 ? folded[..1800] : folded;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", " ").Replace("\n", ") Tj T* (");
}
