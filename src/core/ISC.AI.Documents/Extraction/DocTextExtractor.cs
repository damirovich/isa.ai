using System.Buffers.Binary;
using System.Text;
using ISC.AI.Abstractions.Documents;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Извлекатель текста из <c>.doc</c> — Word 97–2003 (ADR-0042): своим кодом, без сторонних библиотек и программ
/// на сервере. Составной файл OLE (<see cref="CompoundFile"/>) → поток <c>WordDocument</c> и таблица <c>0Table</c>/
/// <c>1Table</c> → текст по таблице кусков (piece table, [MS-DOC] 2.4.1). <c>.doc</c>, который на деле RTF (так
/// сохраняли многие учётные программы), читается разбором RTF (<see cref="RtfText"/>).
/// </summary>
/// <remarks>
/// <para>
/// Абзацы разделяются ПУСТОЙ строкой (<c>\n\n</c>), как у <see cref="DocxTextExtractor"/>: её чанкеры считают
/// границей абзаца (ТО-мат-03). Коды полей (<c>HYPERLINK …</c>, <c>PAGE</c>) отбрасываются, остаётся их результат;
/// ячейки таблиц — через табуляцию; рисунки и служебные знаки удаляются.
/// </para>
/// <para>
/// Не читаются — с понятной ошибкой, а не пустым текстом: <c>.doc</c> с паролем (зашифрован) и форматы Word 6/95
/// (до Word 97). Файл приходит из архива от недоверенного производителя: смещения и длины проверяются, текст
/// ограничен <see cref="MaxCharacters"/>.
/// </para>
/// </remarks>
public sealed class DocTextExtractor : IFormatTextExtractor
{
    /// <summary>Предел извлекаемого текста, символов: защита от «бомбы» в таблице кусков.</summary>
    public const int MaxCharacters = 20_000_000;

    /// <summary>Отказ: документ защищён паролем.</summary>
    public const string EncryptedMessage = "Документ Word защищён паролем — текст не извлекается. Снимите пароль и загрузите заново.";

    /// <summary>Отказ: формат до Word 97.</summary>
    public const string OldFormatMessage =
        "Документ сохранён в формате Word 6/95 — текст не извлекается. Пересохраните его в Word как .docx.";

    // Word 97 записывает nFib = 0x00C1; ранние сборки — 0x00C0. Word 6 — 0x0065, Word 95 — 0x0068.
    private const int MinWord97Fib = 0x00C0;

    // Компактные (8-битные) куски — кодовая страница 1252; байты 0x80–0x9F — по таблице Word ([MS-DOC] 2.4.1).
    private static readonly char[] Cp1252High =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".doc"];

    /// <inheritdoc />
    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return new ExtractedDocument(Extract(buffer.ToArray()));
    }

    /// <summary>Текст документа из байтов файла <c>.doc</c> (Word 97–2003 или RTF).</summary>
    /// <exception cref="InvalidDataException">Файл не Word 97–2003 и не RTF, повреждён, зашифрован или старого формата.</exception>
    public static string Extract(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (RtfText.HasSignature(data))
        {
            return Paragraphs(RtfText.Extract(data));
        }

        if (!CompoundFile.HasSignature(data))
        {
            throw new InvalidDataException("Файл .doc не является документом Word 97–2003 или RTF.");
        }

        var file = CompoundFile.Open(data);
        var word = file.ReadRootStream("WordDocument")
            ?? throw new InvalidDataException("В файле .doc нет потока WordDocument — это не документ Word.");
        return Paragraphs(ReadWordText(file, word));
    }

    private static string ReadWordText(CompoundFile file, byte[] word)
    {
        if (word.Length < 0x01AA || BinaryPrimitives.ReadUInt16LittleEndian(word) != 0xA5EC)
        {
            throw new InvalidDataException("Повреждён заголовок документа Word (FIB).");
        }

        var nFib = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(2));
        if (nFib < MinWord97Fib)
        {
            throw new InvalidDataException(OldFormatMessage);
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        if ((flags & 0x0100) != 0)
        {
            throw new InvalidDataException(EncryptedMessage);
        }

        // FibBase (32 байта) → csw и fibRgW → cslw и fibRgLw → cbRgFcLcb и fibRgFcLcb; fcClx/lcbClx — пара № 33.
        var position = 32;
        var csw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        position += 2 + (csw * 2);
        var cslw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        position += 2 + (cslw * 4);
        var cbRgFcLcb = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        position += 2;
        if (cbRgFcLcb < 34 || position + (34 * 8) > word.Length)
        {
            throw new InvalidDataException("Повреждён заголовок документа Word (FIB).");
        }

        var fcClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(position + (66 * 4)));
        var lcbClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(position + (67 * 4)));

        var table = file.ReadRootStream((flags & 0x0200) != 0 ? "1Table" : "0Table")
            ?? throw new InvalidDataException("В документе Word нет таблицы (0Table/1Table).");
        if (lcbClx == 0 || (long)fcClx + lcbClx > table.Length)
        {
            throw new InvalidDataException("Повреждена таблица кусков документа Word (Clx).");
        }

        var clx = table.AsSpan((int)fcClx, (int)lcbClx);
        var pieces = ReadPieceTable(clx);
        var text = new StringBuilder();
        foreach (var (cpStart, cpEnd, fcRaw) in pieces)
        {
            var count = (long)cpEnd - cpStart;
            if (count <= 0)
            {
                continue;
            }

            if (text.Length + count > MaxCharacters)
            {
                throw new InvalidDataException("Документ Word слишком велик для извлечения текста.");
            }

            var compressed = (fcRaw & 0x40000000) != 0;
            var fc = fcRaw & 0x3FFFFFFF;
            if (compressed)
            {
                var offset = fc / 2;
                if (offset + count > word.Length)
                {
                    throw new InvalidDataException("Кусок текста документа Word за пределами потока.");
                }

                foreach (var b in word.AsSpan((int)offset, (int)count))
                {
                    text.Append(b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b);
                }
            }
            else
            {
                if (fc + (count * 2) > word.Length)
                {
                    throw new InvalidDataException("Кусок текста документа Word за пределами потока.");
                }

                text.Append(Encoding.Unicode.GetString(word, (int)fc, (int)(count * 2)));
            }
        }

        return Clean(text.ToString());
    }

    // Clx: последовательность Prc (0x01) и одна Pcdt (0x02) с PlcPcd: n+1 позиций CP и n описателей кусков по 8 байт.
    private static List<(uint CpStart, uint CpEnd, uint Fc)> ReadPieceTable(ReadOnlySpan<byte> clx)
    {
        var position = 0;
        while (position < clx.Length)
        {
            var kind = clx[position];
            if (kind == 0x01)
            {
                if (position + 3 > clx.Length)
                {
                    break;
                }

                var cbGrpprl = BinaryPrimitives.ReadInt16LittleEndian(clx[(position + 1)..]);
                position += 3 + Math.Max(0, (int)cbGrpprl);
                continue;
            }

            if (kind != 0x02 || position + 5 > clx.Length)
            {
                break;
            }

            var lcb = BinaryPrimitives.ReadUInt32LittleEndian(clx[(position + 1)..]);
            var plc = clx.Slice(position + 5, (int)Math.Min(lcb, (uint)(clx.Length - position - 5)));
            var n = (plc.Length - 4) / 12;
            if (n <= 0)
            {
                break;
            }

            var pieces = new List<(uint, uint, uint)>(n);
            for (var i = 0; i < n; i++)
            {
                var cpStart = BinaryPrimitives.ReadUInt32LittleEndian(plc[(i * 4)..]);
                var cpEnd = BinaryPrimitives.ReadUInt32LittleEndian(plc[((i + 1) * 4)..]);
                var pcd = plc[(((n + 1) * 4) + (i * 8))..];
                pieces.Add((cpStart, cpEnd, BinaryPrimitives.ReadUInt32LittleEndian(pcd[2..])));
            }

            return pieces;
        }

        throw new InvalidDataException("Повреждена таблица кусков документа Word (Clx).");
    }

    // Служебные знаки Word: абзацы, разрывы, ячейки таблиц, поля (код отбрасывается, результат остаётся), рисунки.
    private static string Clean(string raw)
    {
        var output = new StringBuilder(raw.Length);
        var fields = new Stack<bool>(); // true — сейчас идёт код поля (до разделителя 0x14)
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\u0013':
                    fields.Push(true);
                    continue;
                case '\u0014':
                    if (fields.Count > 0)
                    {
                        fields.Pop();
                        fields.Push(false);
                    }

                    continue;
                case '\u0015':
                    if (fields.Count > 0)
                    {
                        fields.Pop();
                    }

                    continue;
            }

            if (fields.Contains(true))
            {
                continue;
            }

            switch (c)
            {
                case '\r' or '\n' or '\u000B' or '\u000C' or '\u000E':
                    output.Append('\n');
                    break;
                case '\u0007':
                    output.Append('\t');
                    break;
                case '\u001E':
                    output.Append('-');
                    break;
                case '\u00A0':
                    output.Append(' ');
                    break;
                case < ' ' and not '\t':
                    break; // рисунки (0x01, 0x08), сноски (0x02), примечания (0x05), мягкий перенос (0x1F)
                default:
                    output.Append(c);
                    break;
            }
        }

        return output.ToString();
    }

    // Абзацы — через пустую строку (граница абзаца для чанкеров), пустые выброшены.
    private static string Paragraphs(string text) =>
        string.Join(
            "\n\n",
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)).Trim();
}
