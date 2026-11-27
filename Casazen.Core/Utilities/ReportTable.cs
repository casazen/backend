using System.Globalization;

namespace Casazen.Core.Utilities;

/// <summary>What a cell of a <see cref="ReportTable"/> holds.</summary>
public enum ReportCellKind
{
    /// <summary>Nothing: an empty cell (a figure nobody reported is empty, never a zero).</summary>
    Empty = 0,

    /// <summary>A text.</summary>
    Text = 1,

    /// <summary>A whole number (a count, a number of nights).</summary>
    Integer = 2,

    /// <summary>A decimal number shown to two places: an amount of money, or a quantity that can be a fraction (the nights of a shared house).</summary>
    Decimal = 3,

    /// <summary>A percentage, held as a fraction (<c>0.455</c> is 45.50%) to the cent of a point.</summary>
    Percent = 4,

    /// <summary>A calendar day.</summary>
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
    /// <summary>How the CSV writes a <see cref="ReportCellKind.Date"/> cell: the one the host reports have always used. A <see cref="DateOnly"/> takes date specifiers only.</summary>
    public const string CsvDateFormat = "dd/MM/yyyy";

    private ReportCell(ReportCellKind kind, string? text = null, long whole = 0, decimal number = 0m, DateOnly day = default)
    {
        Kind = kind;
        TextValue = text;
        WholeValue = whole;
        DecimalValue = number;
        DayValue = day;
    }

    /// <summary>What the cell holds; a default cell is <see cref="ReportCellKind.Empty"/>.</summary>
    public ReportCellKind Kind { get; }

    /// <summary>The text of a <see cref="ReportCellKind.Text"/> cell.</summary>
    public string? TextValue { get; }

    /// <summary>The number of a <see cref="ReportCellKind.Integer"/> cell.</summary>
    public long WholeValue { get; }

    /// <summary>The amount of a <see cref="ReportCellKind.Decimal"/> cell (two places) or the fraction of a <see cref="ReportCellKind.Percent"/> cell (four places).</summary>
    public decimal DecimalValue { get; }

    /// <summary>The day of a <see cref="ReportCellKind.Date"/> cell.</summary>
    public DateOnly DayValue { get; }

    /// <summary>An empty cell.</summary>
    public static ReportCell Empty => default;

    /// <summary>A text cell; <c>null</c> and the empty text are an empty cell.</summary>
    public static ReportCell Text(string? text) => string.IsNullOrEmpty(text) ? default : new ReportCell(ReportCellKind.Text, text: text);

    /// <summary>A whole number; <c>null</c> is an empty cell.</summary>
    public static ReportCell Integer(long? value) => value is { } whole ? new ReportCell(ReportCellKind.Integer, whole: whole) : default;

    /// <summary>An amount of money to two places (rounded away from zero); <c>null</c> is an empty cell, never a zero.</summary>
    public static ReportCell Money(decimal? value) => Rounded(value);

    /// <summary>A number that is not money, to two places (the nights of a house two owners share can be a fraction); <c>null</c> is an empty cell.</summary>
    public static ReportCell Number(decimal? value) => Rounded(value);

    /// <summary>A percentage given as a fraction (0.455 is 45.50%), rounded to four places (the cent of a point); <c>null</c> is an empty cell.</summary>
    public static ReportCell Percent(decimal? fraction) =>
        fraction is { } value ? new ReportCell(ReportCellKind.Percent, number: Math.Round(value, 4, MidpointRounding.AwayFromZero)) : default;

    /// <summary>A calendar day; <c>null</c> is an empty cell.</summary>
    public static ReportCell Date(DateOnly? day) => day is { } value ? new ReportCell(ReportCellKind.Date, day: value) : default;

    /// <summary>A text cell, so a label can be given where a cell is expected.</summary>
    public static implicit operator ReportCell(string? text) => Text(text);

    /// <summary>A whole-number cell, so a count can be given where a cell is expected. A decimal has no such conversion: it is money, a number or a percentage, and the report says which.</summary>
    public static implicit operator ReportCell(int value) => Integer(value);

    /// <summary>The value the CSV writer takes for this cell (<see cref="CsvWriter.Cell"/>): a percentage as the percentage, a day as <see cref="CsvDateFormat"/>.</summary>
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

/// <summary>
/// One table of a report (NF-101, REP-06): its title row, its header and its rows of typed cells. The one definition of a report's columns, written by both
/// <see cref="CsvWriter"/> (tables one under the other, an empty row between them) and <see cref="XlsxWriter"/> (a sheet for each table), so the two files have
/// the same title row, the same columns and the same numbers.
/// </summary>
public sealed class ReportTable
{
    private readonly List<IReadOnlyList<ReportCell>> _rows = [];

    /// <summary>A table with its title row (what the numbers are, on which base and for which days) and its header.</summary>
    public ReportTable(string title, IReadOnlyList<string> header)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(header);

        Title = title;
        Header = header;
    }

    /// <summary>The title row: one text, with the base of the numbers and the days, so no figure travels without its label.</summary>
    public string Title { get; }

    /// <summary>The names of the columns.</summary>
    public IReadOnlyList<string> Header { get; }

    /// <summary>The rows, after the header.</summary>
    public IReadOnlyList<IReadOnlyList<ReportCell>> Rows => _rows;

    /// <summary>The rows the table takes in a file: the title row, the header and the rows.</summary>
    public int RowCount => 2 + _rows.Count;

    /// <summary>Adds a row.</summary>
    public ReportTable Add(params ReportCell[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        _rows.Add(cells);
        return this;
    }

    /// <summary>Adds a row built elsewhere.</summary>
    public ReportTable Add(IReadOnlyList<ReportCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        _rows.Add(cells);
        return this;
    }
}

/// <summary>A sheet of an Excel workbook: the name on its tab and the table it holds.</summary>
/// <param name="Name">The name on the tab; <see cref="XlsxWriter"/> keeps it to what a sheet may be called (31 characters, none of <c>[ ] : * ? / \</c>) and makes two the same differ.</param>
/// <param name="Table">The table of the sheet.</param>
public sealed record ReportSheet(string Name, ReportTable Table);
