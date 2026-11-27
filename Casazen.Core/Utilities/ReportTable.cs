using System.Globalization;

namespace Casazen.Core.Utilities;

public enum ReportCellKind
{
    Empty = 0,

    Text = 1,

    Integer = 2,

    Decimal = 3,

    Percent = 4,

    Date = 5,
}

/// <summary>
/// One typed cell of a <see cref="ReportTable"/> (NF-101, REP-06): the report says what its cell is once, and <see cref="CsvWriter"/> and
/// <see cref="XlsxWriter"/> each write it in their own way, so the CSV and the Excel file of a report carry the same figures. A number is never held
/// as text and never as a floating point: the amounts are <see cref="decimal"/> and are rounded once, here, to the place the files show them.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Text</b> is whatever the report says, a property or a note typed by a person included: both writers keep it from running as a formula
/// (<see cref="CsvWriter.StartsLikeAFormula"/>), the CSV with a leading apostrophe and the Excel file by writing it as a string that is never calculated.</item>
/// <item><b>Decimal</b> (<see cref="Money"/>, <see cref="Number"/>) is rounded to two places, away from zero, the way the CSV has always written it.</item>
/// <item><b>Percent</b> is the fraction rounded to four places (the cent of a point). The CSV shows it as a percentage (<c>45,50</c>, under a header that says
/// <c>(%)</c>), the Excel file holds the fraction with the percent format (<c>45.50%</c>): the same number, written for each file.</item>
/// <item><b>Date</b> is a calendar day: a date cell in the Excel file, <c>dd/mm/yyyy</c> in the CSV (<see cref="CsvDateFormat"/>).</item>
/// </list>
/// </remarks>
public readonly record struct ReportCell
{
    public const string CsvDateFormat = "dd/MM/yyyy";

    private ReportCell(ReportCellKind kind, string? text = null, long whole = 0, decimal number = 0m, DateOnly day = default)
    {
        Kind = kind;
        TextValue = text;
        WholeValue = whole;
        DecimalValue = number;
        DayValue = day;
    }

    public ReportCellKind Kind { get; }

    public string? TextValue { get; }

    public long WholeValue { get; }

    public decimal DecimalValue { get; }

    public DateOnly DayValue { get; }

    public static ReportCell Empty => default;

    public static ReportCell Text(string? text) => string.IsNullOrEmpty(text) ? default : new ReportCell(ReportCellKind.Text, text: text);

    public static ReportCell Integer(long? value) => value is { } whole ? new ReportCell(ReportCellKind.Integer, whole: whole) : default;

    public static ReportCell Money(decimal? value) => Rounded(value);

    public static ReportCell Number(decimal? value) => Rounded(value);

    public static ReportCell Percent(decimal? fraction) =>
        fraction is { } value ? new ReportCell(ReportCellKind.Percent, number: Math.Round(value, 4, MidpointRounding.AwayFromZero)) : default;

    public static ReportCell Date(DateOnly? day) => day is { } value ? new ReportCell(ReportCellKind.Date, day: value) : default;

    public static implicit operator ReportCell(string? text) => Text(text);

    public static implicit operator ReportCell(int value) => Integer(value);

    internal object? ToCsvValue() => Kind switch
    {
        ReportCellKind.Text => TextValue,
        ReportCellKind.Integer => WholeValue,
        ReportCellKind.Decimal => DecimalValue,
        ReportCellKind.Percent => DecimalValue * 100m,
        ReportCellKind.Date => DayValue.ToString(CsvDateFormat, CultureInfo.InvariantCulture),
        _ => null,
    };

    private static ReportCell Rounded(decimal? value) =>
        value is { } amount ? new ReportCell(ReportCellKind.Decimal, number: Math.Round(amount, 2, MidpointRounding.AwayFromZero)) : default;
}

public sealed class ReportTable
{
    private readonly List<IReadOnlyList<ReportCell>> _rows = [];

    public ReportTable(string title, IReadOnlyList<string> header)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(header);

        Title = title;
        Header = header;
    }

    public string Title { get; }

    public IReadOnlyList<string> Header { get; }

    public IReadOnlyList<IReadOnlyList<ReportCell>> Rows => _rows;

    public int RowCount => 2 + _rows.Count;

    public ReportTable Add(params ReportCell[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        _rows.Add(cells);
        return this;
    }

    public ReportTable Add(IReadOnlyList<ReportCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        _rows.Add(cells);
        return this;
    }
}

public sealed record ReportSheet(string Name, ReportTable Table);
