using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>Строка описи — как её записал оператор (значения без разбора, кроме обрезки пробелов).</summary>
/// <param name="RowNumber">Номер строки в файле (заголовок — строка 1): так оператор найдёт её в Excel.</param>
/// <param name="File">Путь файла относительно папки архива.</param>
/// <param name="CaseNumber">Номер дела.</param>
/// <param name="DivisionCode">Код подразделения дела (нужен, если номер повторяется в разных подразделениях).</param>
/// <param name="CapturedAt">Дата съёмки текстом.</param>
/// <param name="Source">Источник.</param>
/// <param name="Place">Место.</param>
/// <param name="Sha256">Контрольная сумма SHA-256.</param>
public sealed record ManifestRow(
    int RowNumber,
    string? File,
    string? CaseNumber,
    string? DivisionCode = null,
    string? CapturedAt = null,
    string? Source = null,
    string? Place = null,
    string? Sha256 = null);

/// <summary>Итог чтения описи.</summary>
/// <param name="Rows">Непустые строки по порядку.</param>
/// <param name="Error">Опись не прочитана целиком (нет обязательных столбцов, слишком велика); строк тогда нет.</param>
/// <param name="Sha256">SHA-256 файла описи (hex, верхний регистр) — в журнал и отчёт (ТБ-077).</param>
/// <param name="EncodingName">Распознанная кодировка — для подсказки оператору.</param>
/// <param name="Delimiter">Распознанный разделитель.</param>
/// <param name="IgnoredColumns">Столбцы, которые опись содержит, но импорт не использует.</param>
public sealed record ManifestReadResult(
    IReadOnlyList<ManifestRow> Rows,
    string? Error,
    string Sha256,
    string EncodingName,
    char Delimiter,
    IReadOnlyList<string> IgnoredColumns);

/// <summary>
/// Опись архива — таблица CSV, которую готовят в Excel (ADR-0041 п. 2): разбор и запись (черновик описи, строки с
/// отказами для повторного импорта).
/// </summary>
/// <remarks>
/// <para>
/// Разделитель определяется по строке заголовка (<c>;</c> — Excel в русской Windows, <c>,</c>, табуляция). Кодировка —
/// UTF-8 (с BOM или без); если файл не является корректным UTF-8 — Windows-1251, в которой сохраняет «CSV (разделители —
/// запятые)» русский Excel. Кавычки — по RFC 4180: поле в кавычках может содержать разделитель и перевод строки,
/// кавычка внутри удваивается.
/// </para>
/// <para>
/// Заголовок — без учёта регистра и буквы «ё»: обязательные столбцы «файл» и «дело»; необязательные «подразделение»
/// (код), «дата съёмки», «источник», «место», «sha256». Лишние столбцы (например, «примечание») не мешают и
/// перечисляются оператору.
/// </para>
/// </remarks>
public static class ImportManifest
{
    /// <summary>Предел размера описи — 50 МБ.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>Предел числа строк описи: больше — разделить на несколько заданий.</summary>
    public const int MaxRows = 200_000;

    /// <summary>Столбец пути файла.</summary>
    public const string FileColumn = "файл";

    /// <summary>Столбец номера дела.</summary>
    public const string CaseColumn = "дело";

    /// <summary>Столбец кода подразделения.</summary>
    public const string DivisionColumn = "подразделение";

    /// <summary>Столбец даты съёмки.</summary>
    public const string CapturedAtColumn = "дата съёмки";

    /// <summary>Столбец источника.</summary>
    public const string SourceColumn = "источник";

    /// <summary>Столбец места.</summary>
    public const string PlaceColumn = "место";

    /// <summary>Столбец контрольной суммы.</summary>
    public const string Sha256Column = "sha256";

    /// <summary>Столбцы описи в порядке записи (черновик и выгрузка отказов).</summary>
    public static IReadOnlyList<string> Columns { get; } =
        [FileColumn, CaseColumn, DivisionColumn, CapturedAtColumn, SourceColumn, PlaceColumn, Sha256Column];

    private enum Field
    {
        File,
        Case,
        Division,
        CapturedAt,
        Source,
        Place,
        Sha256,
    }

    private static readonly Dictionary<string, Field> Aliases = new(StringComparer.Ordinal)
    {
        ["файл"] = Field.File,
        ["путь"] = Field.File,
        ["путь к файлу"] = Field.File,
        ["file"] = Field.File,
        ["path"] = Field.File,
        ["дело"] = Field.Case,
        ["номер дела"] = Field.Case,
        ["№ дела"] = Field.Case,
        ["case"] = Field.Case,
        ["case number"] = Field.Case,
        ["подразделение"] = Field.Division,
        ["код подразделения"] = Field.Division,
        ["division"] = Field.Division,
        ["division code"] = Field.Division,
        ["дата съемки"] = Field.CapturedAt,
        ["дата и время съемки"] = Field.CapturedAt,
        ["дата"] = Field.CapturedAt,
        ["captured at"] = Field.CapturedAt,
        ["captured_at"] = Field.CapturedAt,
        ["источник"] = Field.Source,
        ["source"] = Field.Source,
        ["место"] = Field.Place,
        ["место съемки"] = Field.Place,
        ["place"] = Field.Place,
        ["sha256"] = Field.Sha256,
        ["sha-256"] = Field.Sha256,
        ["контрольная сумма"] = Field.Sha256,
        ["хеш"] = Field.Sha256,
        ["хэш"] = Field.Sha256,
        ["hash"] = Field.Sha256,
    };

    private static readonly string[] DateTimeFormats =
    [
        "d.M.yyyy H:mm:ss", "d.M.yyyy H:mm", "d.M.yyyy H.mm",
        "yyyy-M-d H:mm:ss", "yyyy-M-d H:mm", "yyyy-M-d'T'H:mm:ss", "yyyy-M-d'T'H:mm",
        "d/M/yyyy H:mm:ss", "d/M/yyyy H:mm",
    ];

    private static readonly string[] DateFormats = ["d.M.yyyy", "yyyy-M-d", "d/M/yyyy"];

    /// <summary>Читает опись из байтов файла.</summary>
    /// <param name="content">Содержимое файла описи (не больше <see cref="MaxBytes"/>).</param>
    public static ManifestReadResult Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var sha = Convert.ToHexString(SHA256.HashData(content));
        if (content.LongLength > MaxBytes)
        {
            return Failed($"Опись больше {MaxBytes / (1024 * 1024)} МБ — разделите её на несколько файлов.", sha);
        }

        var (encoding, encodingName) = DetectEncoding(content);
        using var reader = new StreamReader(new MemoryStream(content, writable: false), encoding, detectEncodingFromByteOrderMarks: true);

        var header = ReadRecord(reader, delimiter: null, out var delimiter);
        if (header is null || header.All(string.IsNullOrWhiteSpace))
        {
            return Failed("Опись пуста: в первой строке должны быть названия столбцов «файл» и «дело».", sha, encodingName, delimiter);
        }

        var map = new Dictionary<Field, int>();
        var ignored = new List<string>();
        for (var i = 0; i < header.Count; i++)
        {
            var name = NormalizeHeader(header[i]);
            if (name.Length == 0)
            {
                continue;
            }

            if (Aliases.TryGetValue(name, out var field) && !map.ContainsKey(field))
            {
                map[field] = i;
            }
            else
            {
                ignored.Add(header[i].Trim());
            }
        }

        if (!map.ContainsKey(Field.File) || !map.ContainsKey(Field.Case))
        {
            return Failed(
                "В первой строке описи нет обязательных столбцов «файл» и «дело». Разделитель столбцов — «;», «,» или табуляция.",
                sha, encodingName, delimiter, ignored);
        }

        var rows = new List<ManifestRow>();
        var recordNumber = 1;
        while (ReadRecord(reader, delimiter, out _) is { } record)
        {
            recordNumber++;
            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (rows.Count >= MaxRows)
            {
                return Failed(
                    $"В описи больше {MaxRows.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} строк — разделите её на несколько заданий.",
                    sha, encodingName, delimiter, ignored);
            }

            rows.Add(new ManifestRow(
                recordNumber,
                Value(record, map, Field.File),
                Value(record, map, Field.Case),
                Value(record, map, Field.Division),
                Value(record, map, Field.CapturedAt),
                Value(record, map, Field.Source),
                Value(record, map, Field.Place),
                Value(record, map, Field.Sha256)));
        }

        return new ManifestReadResult(rows, null, sha, encodingName, delimiter, ignored);
    }

    /// <summary>
    /// Разбирает дату съёмки из описи. Без смещения — местное время пояса <paramref name="zone"/>; только дата — 00:00
    /// того же пояса (<paramref name="dateOnly"/>). Форматы: «дд.мм.гггг [чч:мм[:сс]]», «гггг-мм-дд [чч:мм[:сс]]»,
    /// ISO 8601 со смещением.
    /// </summary>
    public static bool TryParseCapturedAt(string? text, TimeZoneInfo zone, out DateTimeOffset value, out bool dateOnly)
    {
        ArgumentNullException.ThrowIfNull(zone);
        value = default;
        dateOnly = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // Со смещением или «Z» — момент задан точно, пояс сервера не нужен.
        if ((trimmed.EndsWith('Z') || HasOffset(trimmed))
            && DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact))
        {
            value = exact;
            return true;
        }

        if (DateTime.TryParseExact(trimmed, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
        {
            value = new DateTimeOffset(local, zone.GetUtcOffset(local));
            return true;
        }

        if (DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
        {
            value = new DateTimeOffset(date.Date, zone.GetUtcOffset(date.Date));
            dateOnly = true;
            return true;
        }

        return false;
    }

    /// <summary>Контрольная сумма из описи в виде hex верхнего регистра или <see langword="null"/>, если это не SHA-256.</summary>
    public static string? NormalizeSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        return trimmed.Length == 64 && trimmed.All(char.IsAsciiHexDigit) ? trimmed.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Записывает опись: UTF-8 с BOM (Excel тогда открывает кириллицу без вопроса о кодировке), разделитель «;».
    /// </summary>
    public static async Task WriteAsync(Stream output, IAsyncEnumerable<ManifestRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(rows);

        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await writer.WriteLineAsync(string.Join(';', Columns.Select(EscapeCsv)));
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            await writer.WriteLineAsync(string.Join(';', new[]
            {
                row.File, row.CaseNumber, row.DivisionCode, row.CapturedAt, row.Source, row.Place, row.Sha256,
            }.Select(EscapeCsv)));
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static ManifestReadResult Failed(
        string error, string sha, string encodingName = "UTF-8", char delimiter = ';', IReadOnlyList<string>? ignored = null) =>
        new([], error, sha, encodingName, delimiter, ignored ?? []);

    private static (Encoding Encoding, string Name) DetectEncoding(byte[] content)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetCharCount(content);
            return (new UTF8Encoding(false), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (CodePagesEncodingProvider.Instance.GetEncoding(1251) ?? Encoding.Latin1, "Windows-1251");
        }
    }

    private static string NormalizeHeader(string header) =>
        string.Join(' ', header.Trim().TrimStart('﻿').ToLowerInvariant().Replace('ё', 'е')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string? Value(List<string> record, Dictionary<Field, int> map, Field field)
    {
        if (!map.TryGetValue(field, out var index) || index >= record.Count)
        {
            return null;
        }

        var value = record[index].Trim();

        // Апостроф, которым запись описи экранирует «формулу» для Excel (см. Escape), снимается: путь и номер должны
        // вернуться такими, какими были.
        if (value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@')
        {
            value = value[1..];
        }

        return value.Length == 0 ? null : value;
    }

    private static bool HasOffset(string text)
    {
        // «…+06:00», «…-03:30» после времени; дата «2023-05-14» сама по себе смещения не содержит.
        var t = text.IndexOf('T', StringComparison.Ordinal);
        var timePart = t >= 0 ? text[(t + 1)..] : (text.IndexOf(' ', StringComparison.Ordinal) is var s and >= 0 ? text[(s + 1)..] : string.Empty);
        return timePart.Contains('+', StringComparison.Ordinal) || timePart.Contains('-', StringComparison.Ordinal);
    }

    // Запись CSV по RFC 4180. Разделитель — из заголовка (delimiter == null — определить по нему).
    private static List<string>? ReadRecord(TextReader reader, char? delimiter, out char detected)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        detected = delimiter ?? ';';

        // Заголовок читается целиком, чтобы выбрать разделитель по числу вхождений вне кавычек.
        if (delimiter is null)
        {
            var line = ReadRawRecord(reader);
            if (line is null)
            {
                return null;
            }

            detected = DetectDelimiter(line);
            using var lineReader = new StringReader(line);
            return ReadRecord(lineReader, detected, out _) ?? [];
        }

        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            any = true;
            var c = (char)ch;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        current.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c == '"' && current.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else if (c == '\r')
            {
                if (reader.Peek() == '\n')
                {
                    reader.Read();
                }

                break;
            }
            else if (c == '\n')
            {
                break;
            }
            else
            {
                current.Append(c);
            }
        }

        if (!any)
        {
            return null;
        }

        fields.Add(current.ToString());
        return fields;
    }

    // Сырая первая запись (с учётом кавычек, переводы строк внутри кавычек сохраняются).
    private static string? ReadRawRecord(TextReader reader)
    {
        var line = new StringBuilder();
        var quoted = false;
        var any = false;
        int ch;
        while ((ch = reader.Read()) >= 0)
        {
            any = true;
            var c = (char)ch;
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                break;
            }

            line.Append(c);
        }

        return any ? line.ToString() : null;
    }

    private static char DetectDelimiter(string header)
    {
        var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in header)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && counts.TryGetValue(c, out var count))
            {
                counts[c] = count + 1;
            }
        }

        var best = counts.OrderByDescending(p => p.Value).First();
        return best.Value > 0 ? best.Key : ';';
    }

    /// <summary>
    /// Значение для CSV: в кавычках, если в нём разделитель, кавычка или перевод строки. Значение, которое Excel принял
    /// бы за формулу (<c>=</c>, <c>+</c>, <c>@</c>, <c>-</c> не перед цифрой), экранируется апострофом — опись и отчёт
    /// строятся из имён файлов архива и не должны ничего вычислять при открытии; при чтении описи апостроф снимается.
    /// </summary>
    internal static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var safe = value[0] is '=' or '+' or '@' || (value[0] == '-' && value.Length > 1 && !char.IsAsciiDigit(value[1]))
            ? "'" + value
            : value;
        return safe.IndexOfAny([';', '"', '\r', '\n', ',', '\t']) >= 0
            ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : safe;
    }
}
