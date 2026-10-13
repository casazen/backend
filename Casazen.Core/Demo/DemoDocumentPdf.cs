using System.Text;

namespace Casazen.Core.Demo;

/// <summary>
/// A one-page PDF whose text says it is test data. The bytes start with <c>%PDF</c> and are a real document of the
/// type the activation asks for (CIN certificate, APE, licence): nothing in them is a copy of an official act.
/// </summary>
public static class DemoDocumentPdf
{
    public const string Marker = "DOCUMENTO DI TEST";

    public static byte[] Create(string heading, string detail)
    {
        var stream = $"BT /F1 14 Tf 48 760 Td ({PdfText(heading)}) Tj 0 -24 Td ({PdfText(detail)}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var builder = new StringBuilder();
        builder.Append("%PDF-1.4\n");
        var offsets = new int[objects.Length + 1];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = Encoding.ASCII.GetByteCount(builder.ToString());
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        builder.Append("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Length; i++)
            builder.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n");
        builder.Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string PdfText(string value)
    {
        var ascii = new string(value.Select(c => c is >= (char)32 and <= (char)126 ? c : ' ').ToArray());
        return ascii.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
    }
}
