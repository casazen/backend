using System.Globalization;
using System.Text;
using Casazen.Core.Exceptions;

namespace Casazen.Core.BankStatements;

/// <summary>
/// Reads the movements out of a table (a CSV or the first sheet of an Excel file), whatever the bank calls its columns: the title row is
/// looked for among the first rows and its words are matched against the ones Italian banks use («Data valuta», «Descrizione operazione»,
/// «Importo», or «Dare»/«Avere»); when nothing is recognized the landlord says which column is which (<see cref="BankColumnMapping"/>).
/// </summary>
internal static class TabularStatementReader
{
    private const int HeaderSearchRows = 40;
    private const int PreviewRows = 6;
    private const int PreviewColumns = 12;
    private const int PreviewCellLength = 40;

    private enum Role
    {
        BookingDate,
        ValueDate,
        Amount,
        Credit,
        Debit,
        Sign,
        Description,
        Counterparty,
        Iban,
        Currency,
        Reference,
    }

    /// <summary>Where each piece of a movement is in a row.</summary>
    private sealed class ColumnMap
    {
        public int? BookingDate { get; set; }

        public int? ValueDate { get; set; }

        public int? Amount { get; set; }

        public int? Credit { get; set; }

        public int? Debit { get; set; }

        public int? Sign { get; set; }

        public List<int> Descriptions { get; } = [];

        public int? Counterparty { get; set; }

        public int? Iban { get; set; }

        public int? Currency { get; set; }

        public int? Reference { get; set; }

        public bool HasDate => BookingDate is not null || ValueDate is not null;

        public bool HasAmount => Amount is not null || Credit is not null;
    }

    public static ParsedBankStatement Read(BankStatementFormat format, IReadOnlyList<Cell[]> allRows, BankColumnMapping? mapping)
    {
        var rows = allRows.Where(row => row.Any(cell => !cell.IsBlank)).ToList();
        if (rows.Count > BankStatementLimits.MaxRows)
            throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);

        ColumnMap map;
        int headerRow;
        if (mapping is not null)
        {
            (map, headerRow) = FromMapping(mapping, rows);
        }
        else if (!TryDetect(rows, out map, out headerRow))
        {
            throw Unrecognized(rows);
        }

        var amountMark = DetectMark(rows, headerRow, map);
        var credits = new List<BankMovement>();
        var movements = 0;
        for (var index = headerRow + 1; index < rows.Count; index++)
        {
            var row = rows[index];
            if (!TryReadMovement(row, map, amountMark, out var movement))
                continue;

            movements++;
            if (movement.Amount <= 0m || movement.Currency != "EUR")
                continue;
            credits.Add(movement);
            if (credits.Count > BankStatementLimits.MaxCredits)
                throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);
        }

        if (movements == 0)
            throw new DomainRuleException(BankStatementErrorCodes.Empty, "BankStatementEmpty");
        return new ParsedBankStatement(format, credits, movements);
    }

    // ---------------------------------------------------------------- columns

    private static bool TryDetect(IReadOnlyList<Cell[]> rows, out ColumnMap map, out int headerRow)
    {
        for (var index = 0; index < Math.Min(rows.Count, HeaderSearchRows); index++)
        {
            var candidate = MapFromTitles(rows[index]);
            if (candidate.HasDate && candidate.HasAmount && candidate.Descriptions.Count > 0)
            {
                map = candidate;
                headerRow = index;
                return true;
            }
        }

        // A statement without any description column still names the payer or the reason somewhere: accept it without one only when the
        // dates and the amounts are there, so the landlord is not asked to map a file that is readable.
        for (var index = 0; index < Math.Min(rows.Count, HeaderSearchRows); index++)
        {
            var candidate = MapFromTitles(rows[index]);
            if (candidate.HasDate && candidate.HasAmount)
            {
                map = candidate;
                headerRow = index;
                return true;
            }
        }

        map = new ColumnMap();
        headerRow = -1;
        return false;
    }

    private static ColumnMap MapFromTitles(IReadOnlyList<Cell> titles)
    {
        var map = new ColumnMap();
        for (var column = 0; column < titles.Count; column++)
        {
            if (Classify(titles[column].Text) is not { } role)
                continue;

            switch (role)
            {
                case Role.BookingDate:
                    map.BookingDate ??= column;
                    break;
                case Role.ValueDate:
                    map.ValueDate ??= column;
                    break;
                case Role.Amount:
                    map.Amount ??= column;
                    break;
                case Role.Credit:
                    map.Credit ??= column;
                    break;
                case Role.Debit:
                    map.Debit ??= column;
                    break;
                case Role.Sign:
                    map.Sign ??= column;
                    break;
                case Role.Description:
                    map.Descriptions.Add(column);
                    break;
                case Role.Counterparty:
                    map.Counterparty ??= column;
                    break;
                case Role.Iban:
                    map.Iban ??= column;
                    break;
                case Role.Currency:
                    map.Currency ??= column;
                    break;
                case Role.Reference:
                    map.Reference ??= column;
                    break;
            }
        }

        return map;
    }

    /// <summary>The role a title plays, by the words banks use for it; a title the importer does not know is null.</summary>
    private static Role? Classify(string title)
    {
        var text = BankValues.NormalizeText(title);
        if (text.Length == 0)
            return null;

        if (text is "valuta" or "data valuta" or "data val" or "val" or "value date" or "valuta data" || text.StartsWith("data valuta", StringComparison.Ordinal))
            return Role.ValueDate;
        if (text is "data" or "date" or "giorno" or "data contabile" or "data operazione" or "data registrazione" or "data movimento"
            or "data transazione" or "data cont" or "data op" or "booking date" or "data contabilizzazione" or "data effettuazione"
            or "data operazione contabile" or "data di registrazione" or "transaction date")
        {
            return Role.BookingDate;
        }

        if (text.Contains("iban", StringComparison.Ordinal))
            return Role.Iban;
        if (text is "divisa" or "currency" or "ccy" or "codice divisa" or "valuta importo")
            return Role.Currency;
        if (text is "segno" or "dare avere" or "d a" or "dav" or "c d" or "segno movimento" or "tipo movimento" or "tipo operazione")
            return Role.Sign;

        if (text is "avere" or "entrate" or "accrediti" or "accredito" or "crediti" or "credito" or "credit" or "in entrata" or "entrata"
            or "importo avere" or "importo accredito" or "importo entrata" or "importo accrediti" or "importo in entrata" or "versamenti")
        {
            return Role.Credit;
        }

        if (text is "dare" or "uscite" or "addebiti" or "addebito" or "debiti" or "debito" or "debit" or "in uscita" or "uscita"
            or "importo dare" or "importo addebito" or "importo uscita" or "importo addebiti" or "importo in uscita" or "prelievi")
        {
            return Role.Debit;
        }

        if (text.StartsWith("importo", StringComparison.Ordinal) || text is "amount" or "ammontare")
            return Role.Amount;

        if (text.Contains("ordinante", StringComparison.Ordinal) || text is "controparte" or "nome controparte" or "intestatario" or "debtor"
            or "payer" or "nome ordinante" or "pagatore" or "mittente")
        {
            return Role.Counterparty;
        }

        if (text is "cro" or "trn" or "id transazione" or "id operazione" or "numero operazione" or "codice operazione" or "reference id"
            or "riferimento banca" or "cro trn")
        {
            return Role.Reference;
        }

        if (text.Contains("descrizione", StringComparison.Ordinal) || text.Contains("causale", StringComparison.Ordinal)
            || text.Contains("dettagli", StringComparison.Ordinal) || text.Contains("dettaglio", StringComparison.Ordinal)
            || text is "note" or "operazione" or "motivo" or "description" or "details" or "informazioni" or "testo" or "concept" or "riferimento"
            or "reference" or "memo" or "narrative" or "info")
        {
            return Role.Description;
        }

        return null;
    }

    private static (ColumnMap Map, int HeaderRow) FromMapping(BankColumnMapping mapping, IReadOnlyList<Cell[]> rows)
    {
        var width = rows.Count == 0 ? 0 : rows.Max(row => row.Length);
        static bool Fits(int? column, int width) => column is null || (column >= 0 && column < width && column < BankStatementLimits.MaxColumns);

        if (mapping.HeaderRow < -1
            || mapping.HeaderRow >= rows.Count
            || !Fits(mapping.Date, width) || mapping.Date < 0
            || !Fits(mapping.Description, width) || mapping.Description < 0
            || !Fits(mapping.ValueDate, width)
            || !Fits(mapping.Amount, width)
            || !Fits(mapping.Credit, width)
            || !Fits(mapping.Debit, width)
            || !Fits(mapping.Counterparty, width)
            || !Fits(mapping.Iban, width)
            || (mapping.Amount is null && mapping.Credit is null))
        {
            throw new DomainRuleException(BankStatementErrorCodes.MappingInvalid, "BankStatementMappingInvalid");
        }

        var map = new ColumnMap
        {
            BookingDate = mapping.Date,
            ValueDate = mapping.ValueDate,
            Amount = mapping.Amount,
            Credit = mapping.Credit,
            Debit = mapping.Debit,
            Counterparty = mapping.Counterparty,
            Iban = mapping.Iban,
        };
        map.Descriptions.Add(mapping.Description);

        // What the landlord did not say but the titles do (payer, IBAN, currency, sign) is still used.
        if (mapping.HeaderRow >= 0)
        {
            var detected = MapFromTitles(rows[mapping.HeaderRow]);
            map.Counterparty ??= detected.Counterparty;
            map.Iban ??= detected.Iban;
            map.Currency ??= detected.Currency;
            map.Sign ??= detected.Sign;
            map.Reference ??= detected.Reference;
            map.Debit ??= mapping.Credit is not null ? detected.Debit : null;
            foreach (var column in detected.Descriptions.Where(column => !map.Descriptions.Contains(column)))
                map.Descriptions.Add(column);
        }

        return (map, mapping.HeaderRow);
    }

    private static DomainRuleException Unrecognized(IReadOnlyList<Cell[]> rows)
    {
        var headerRow = GuessHeaderRow(rows);
        // The top of the file as it is, and at least down to the first movement under the titles the guess found: a long preamble
        // (holder, account, period, balance) must not hide the very rows the landlord needs to see.
        var shown = Math.Max(PreviewRows, headerRow + 2);
        var preview = rows
            .Take(shown)
            .Select(row => row.Take(PreviewColumns).Select(cell => BankValues.Clip(cell.Text, PreviewCellLength)).ToArray())
            .ToArray();
        return new DomainRuleException(BankStatementErrorCodes.ColumnsUnrecognized, "BankStatementColumnsUnrecognized")
        {
            Extensions = new Dictionary<string, object?>
            {
                ["headerRow"] = headerRow,
                ["columnCount"] = rows.Count == 0 ? 0 : Math.Min(PreviewColumns, rows.Take(shown).Max(row => row.Length)),
                ["rows"] = preview,
            },
        };
    }

    /// <summary>The first of the top rows that looks like titles (several cells, none a date or an amount); -1 when the file seems to have none.</summary>
    private static int GuessHeaderRow(IReadOnlyList<Cell[]> rows)
    {
        for (var index = 0; index < Math.Min(rows.Count, 10); index++)
        {
            var filled = rows[index].Where(cell => !cell.IsBlank).ToList();
            if (filled.Count >= 3 && filled.All(cell => !BankValues.TryParseDate(cell, out _) && !BankValues.TryParseAmount(cell, DecimalMark.Comma, out _)))
                return index;
        }

        return -1;
    }

    // ---------------------------------------------------------------- rows

    private static DecimalMark DetectMark(IReadOnlyList<Cell[]> rows, int headerRow, ColumnMap map)
    {
        var columns = new[] { map.Amount, map.Credit, map.Debit }.Where(column => column is not null).Select(column => column!.Value).ToList();
        var cells = rows.Skip(headerRow + 1).SelectMany(row => columns.Select(column => column < row.Length ? row[column] : Cell.Empty));
        return BankValues.DetectDecimalMark(cells);
    }

    private static bool TryReadMovement(Cell[] row, ColumnMap map, DecimalMark mark, out BankMovement movement)
    {
        movement = null!;

        var hasBooking = BankValues.TryParseDate(At(row, map.BookingDate), out var booking);
        var hasValue = BankValues.TryParseDate(At(row, map.ValueDate), out var value);
        if (!hasBooking && !hasValue)
            return false;
        if (!hasBooking)
            booking = value;
        if (!hasValue)
            value = booking;

        if (!TryReadAmount(row, map, mark, out var amount))
            return false;

        var description = BankValues.JoinDistinct(map.Descriptions.Select(column => At(row, column).Text));
        var counterparty = BankValues.Clip(At(row, map.Counterparty).Text, 200);
        var iban = BankValues.NormalizeIban(At(row, map.Iban).Text)
            ?? BankValues.FindIban($"{description} {counterparty}");
        var reference = BankValues.Clip(At(row, map.Reference).Text, 100);

        movement = new BankMovement(
            booking,
            value,
            amount,
            BankValues.NormalizeCurrency(At(row, map.Currency).Text),
            description,
            counterparty.Length == 0 ? null : counterparty,
            iban,
            reference.Length == 0 ? null : reference);
        return true;
    }

    private static bool TryReadAmount(Cell[] row, ColumnMap map, DecimalMark mark, out decimal amount)
    {
        amount = 0m;
        if (map.Amount is not null)
        {
            if (!BankValues.TryParseAmount(At(row, map.Amount), mark, out amount))
                return false;
        }
        else
        {
            // Money in and money out in two columns: what comes in is positive, what goes out is taken off whatever sign it is written with.
            var hasCredit = BankValues.TryParseAmount(At(row, map.Credit), mark, out var credit);
            var hasDebit = BankValues.TryParseAmount(At(row, map.Debit), mark, out var debit);
            if (!hasCredit && !hasDebit)
                return false;
            amount = (hasCredit ? credit : 0m) - (hasDebit ? Math.Abs(debit) : 0m);
        }

        if (map.Sign is not null)
        {
            // A column that holds only «+» or «-» (or the long dashes a spreadsheet makes of it) has nothing left once the text is normalized.
            var written = At(row, map.Sign).Text.Trim();
            var sign = written switch
            {
                "-" or "−" or "–" or "—" => "meno",
                "+" => "piu",
                _ => BankValues.NormalizeText(written),
            };
            if (sign is "d" or "dare" or "db" or "dr" or "debit" or "addebito" or "uscita" or "meno")
                amount = -Math.Abs(amount);
            else if (sign is "a" or "avere" or "c" or "cr" or "cd" or "credit" or "accredito" or "entrata" or "piu")
                amount = Math.Abs(amount);
        }

        return true;
    }

    private static Cell At(Cell[] row, int? column) =>
        column is { } index && index >= 0 && index < row.Length ? row[index] : Cell.Empty;
}

/// <summary>Reads the text of a CSV (or a tab- or pipe-separated file) into rows of cells.</summary>
internal static class CsvTableReader
{
    private static readonly char[] Delimiters = [';', '\t', '|', ','];
    private const int DelimiterSampleLines = 50;

    public static List<Cell[]> Read(string text)
    {
        var delimiter = DetectDelimiter(text);
        var rows = new List<Cell[]>();
        var row = new List<Cell>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldHasContent = false;

        void EndField()
        {
            if (row.Count < BankStatementLimits.MaxColumns)
                row.Add(new Cell(BankValues.Clip(field.ToString()), false));
            field.Clear();
            fieldHasContent = false;
        }

        void EndRow()
        {
            EndField();
            if (row.Any(cell => !cell.IsBlank))
                rows.Add(row.ToArray());
            row.Clear();
            if (rows.Count > BankStatementLimits.MaxRows)
                throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    Append(ch);
                }

                continue;
            }

            if (ch == '"' && !fieldHasContent)
            {
                inQuotes = true;
                fieldHasContent = true;
            }
            else if (ch == delimiter)
            {
                EndField();
            }
            else if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                EndRow();
            }
            else if (ch == '\n')
            {
                EndRow();
            }
            else
            {
                Append(ch);
            }
        }

        if (field.Length > 0 || row.Count > 0)
            EndRow();
        return rows;

        void Append(char ch)
        {
            fieldHasContent = true;
            // A field is never longer than the importer keeps; the rest of a runaway cell is not stored.
            if (field.Length < BankStatementLimits.MaxTextLength * 2)
                field.Append(ch);
        }
    }

    /// <summary>
    /// The delimiter that splits the most lines into the same number of cells (so a title line with a stray comma does not decide);
    /// <c>;</c> when none splits anything.
    /// </summary>
    private static char DetectDelimiter(string text)
    {
        var lines = new List<string>(DelimiterSampleLines);
        using (var reader = new StringReader(text))
        {
            while (lines.Count < DelimiterSampleLines && reader.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(line);
            }
        }

        var best = ';';
        var bestScore = 0;
        foreach (var candidate in Delimiters)
        {
            var counts = lines.Select(line => CountOutsideQuotes(line, candidate)).Where(count => count > 0).ToList();
            if (counts.Count == 0)
                continue;

            var modal = counts.GroupBy(count => count).OrderByDescending(group => group.Count()).ThenByDescending(group => group.Key).First();
            var score = modal.Count() * modal.Key;
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"')
                quoted = !quoted;
            else if (ch == delimiter && !quoted)
                count++;
        }

        return count;
    }
}

/// <summary>Turns the bytes of a text file into text: UTF-8 (with or without BOM), UTF-16 with BOM, or the Windows-1252 that older bank exports use.</summary>
internal static class StatementText
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetString(bytes);
        }
    }
}
