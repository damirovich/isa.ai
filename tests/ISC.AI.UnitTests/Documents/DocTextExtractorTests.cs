using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Documents.Extraction;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Documents;

/// <summary>
/// Чтение текста <c>.doc</c> (ADR-0042): Word 97–2003 по таблице кусков внутри составного файла OLE и <c>.doc</c>,
/// который на деле RTF. Файлы Word собираются в тесте по спецификациям [MS-CFB] и [MS-DOC] (<see cref="SyntheticDoc"/>):
/// в репозитории нет ни чужих документов, ни зависимости от установленного Word.
/// </summary>
public sealed class DocTextExtractorTests
{
    [Fact(DisplayName = "Word 97: 8-битный и Unicode куски — латиница, кириллица и киргизские буквы; абзацы через пустую строку")]
    public void Reads_compressed_and_unicode_pieces()
    {
        var doc = SyntheticDoc.Build(
            SyntheticDoc.Piece.Compressed("Report 12/2023\r"),
            SyntheticDoc.Piece.Unicode("Сводка: объект прибыл к адресу ул. Ленина, 5.\rКыргызча: өзгөчө үй ң.\r"));

        DocTextExtractor.Extract(doc).ShouldBe(
            "Report 12/2023\n\nСводка: объект прибыл к адресу ул. Ленина, 5.\n\nКыргызча: өзгөчө үй ң.");
    }

    [Fact(DisplayName = "Word 97: таблица — ячейки через табуляцию, коды полей отброшены (остаётся результат), рисунки и служебные знаки удалены")]
    public void Cleans_tables_fields_and_special_characters()
    {
        var doc = SyntheticDoc.Build(SyntheticDoc.Piece.Unicode(
            "Марка\u0007Госномер\u0007\u0007\rТойота\u0007\u0001\u000801KG123\u0007\u0007\r"
            + "См. \u0013 HYPERLINK \"http://x\" \u0014ссылку\u0015 и \u0013PAGE\u00143\u0015.\u000BДалее\u001Eстраница\r"));

        DocTextExtractor.Extract(doc).ShouldBe(
            "Марка\tГосномер\n\nТойота\t01KG123\n\nСм. ссылку и 3.\n\nДалее-страница");
    }

    [Fact(DisplayName = "Word 97: 8-битный кусок с типографскими кавычками и тире — по таблице Word (1252)")]
    public void Compressed_piece_uses_word_table()
    {
        var doc = SyntheticDoc.Build(SyntheticDoc.Piece.Compressed("\u0093quoted\u0094 \u0097 dash\r", raw: [0x93, 0x71, 0x94, 0x20, 0x97, 0x20, 0x64, 0x0D]));

        DocTextExtractor.Extract(doc).ShouldBe("“q” — d");
    }

    [Fact(DisplayName = "Таблица кусков из «0Table», поток WordDocument в обычных секторах, таблица — в мини-потоке")]
    public void Reads_zero_table_and_mini_stream()
    {
        var doc = SyntheticDoc.Build([SyntheticDoc.Piece.Unicode("Нулевая таблица\r")], whichTable: 0);

        DocTextExtractor.Extract(doc).ShouldBe("Нулевая таблица");
    }

    [Fact(DisplayName = "Документ с паролем — понятный отказ, не пустой текст")]
    public void Encrypted_document_is_rejected() =>
        Should.Throw<InvalidDataException>(() => DocTextExtractor.Extract(SyntheticDoc.Build([SyntheticDoc.Piece.Unicode("тайна\r")], encrypted: true)))
            .Message.ShouldBe(DocTextExtractor.EncryptedMessage);

    [Fact(DisplayName = "Word 6/95 — понятный отказ «пересохраните как .docx»")]
    public void Old_word_format_is_rejected() =>
        Should.Throw<InvalidDataException>(() => DocTextExtractor.Extract(SyntheticDoc.Build([SyntheticDoc.Piece.Unicode("старый\r")], nFib: 0x0068)))
            .Message.ShouldBe(DocTextExtractor.OldFormatMessage);

    [Theory(DisplayName = "Не Word и не RTF, обрезанный и испорченный составной файл — InvalidDataException, а не сбой или зависание")]
    [InlineData("plain")]
    [InlineData("truncated")]
    [InlineData("bad-chain")]
    [InlineData("bad-clx")]
    public void Malformed_files_are_rejected(string kind)
    {
        var good = SyntheticDoc.Build(SyntheticDoc.Piece.Unicode("текст\r"));
        var data = kind switch
        {
            "plain" => Encoding.UTF8.GetBytes("просто текст, не документ Word"),
            "truncated" => good[..1000],
            "bad-chain" => SyntheticDoc.Corrupt(good, fatEntryForFirstStreamSector: 3), // сектор ссылается сам на себя
            _ => SyntheticDoc.Build([SyntheticDoc.Piece.Unicode("текст\r")], corruptClx: true),
        };

        Should.Throw<InvalidDataException>(() => DocTextExtractor.Extract(data));
    }

    [Fact(DisplayName = "RTF под видом .doc: кириллица по \\ansicpg и \\fcharset204, \\uN, служебные группы пропущены, абзацы и ячейки")]
    public void Reads_rtf_disguised_as_doc()
    {
        const string rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil\fcharset204 Times New Roman;}{\f1\fnil\fcharset0 Arial;}}"
            + @"{\colortbl;\red0\green0\blue0;}{\*\generator Test;}{\info{\title Скрытое}}"
            + @"\pard\f0 \'d1\'e2\'ee\'e4\'ea\'e0 \'b9 7\par"
            + @"\f1 Tab\tab cell\cell end\par"
            + @"\u1257?\u1199?\u1187? {\field{\*\fldinst HYPERLINK x}{\fldrslt result}}\par"
            + @"\'7b escaped \{ \} \\ done}";

        DocTextExtractor.Extract(Encoding.ASCII.GetBytes(rtf)).ShouldBe(
            "Сводка № 7\n\nTab\tcell\tend\n\nөүң result\n\n{ escaped { } \\ done");
    }

    [Fact(DisplayName = "Фасад извлечения выбирает .doc по расширению — текст .doc идёт в корпус и поиск")]
    public async Task Composite_extractor_handles_doc()
    {
        var composite = new CompositeTextExtractor([new PlainTextExtractor(), new DocxTextExtractor(), new DocTextExtractor()]);
        composite.CanExtract("сводка.DOC").ShouldBeTrue();

        using var stream = new MemoryStream(SyntheticDoc.Build(SyntheticDoc.Piece.Unicode("Привет\r")));
        (await composite.ExtractAsync(stream, "сводка.doc")).Text.ShouldBe("Привет");
    }

    /// <summary>
    /// Сборщик минимального документа Word 97 по спецификациям: составной файл OLE версии 3 (сектор 512 байт) с потоком
    /// <c>WordDocument</c> (заголовок FIB и текст, больше порога мини-потока — в обычных секторах) и таблицей
    /// <c>1Table</c>/<c>0Table</c> с Clx (меньше порога — в мини-потоке).
    /// </summary>
    internal static class SyntheticDoc
    {
        private const uint End = 0xFFFFFFFE;
        private const uint Free = 0xFFFFFFFF;
        private const uint FatSector = 0xFFFFFFFD;
        private const uint NoStream = 0xFFFFFFFF;
        private const int TextOffset = 0x800;

        /// <summary>Кусок текста: 8-битный (компактный) или UTF-16.</summary>
        public sealed record Piece(string Text, bool IsCompressed, byte[]? Raw = null)
        {
            public static Piece Compressed(string text, byte[]? raw = null) => new(text, true, raw);

            public static Piece Unicode(string text) => new(text, false);

            public byte[] Bytes => Raw ?? (IsCompressed ? Text.Select(c => (byte)c).ToArray() : Encoding.Unicode.GetBytes(Text));

            public int CharCount => IsCompressed ? Bytes.Length : Text.Length;
        }

        public static byte[] Build(params Piece[] pieces) => Build(pieces, whichTable: 1);

        public static byte[] Build(Piece[] pieces, int whichTable = 1, bool encrypted = false, ushort nFib = 0x00C1, bool corruptClx = false)
        {
            // Поток WordDocument: FIB + текст кусков с 0x800, дополненный до 5000 байт (больше порога мини-потока).
            var word = new byte[Math.Max(5000, TextOffset + pieces.Sum(p => p.Bytes.Length) + 16)];
            BinaryPrimitives.WriteUInt16LittleEndian(word, 0xA5EC);
            BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(2), nFib);
            ushort flags = 0;
            if (whichTable == 1)
            {
                flags |= 0x0200;
            }

            if (encrypted)
            {
                flags |= 0x0100;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(0x0A), flags);
            BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(0x20), 14);          // csw
            BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(0x3E), 22);          // cslw
            BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(0x98), 93);          // cbRgFcLcb (Word 97)

            var cps = new List<uint> { 0 };
            var pcds = new List<(uint Fc, bool Compressed)>();
            var offset = TextOffset;
            foreach (var piece in pieces)
            {
                piece.Bytes.CopyTo(word, offset);
                pcds.Add(((uint)offset, piece.IsCompressed));
                offset += piece.Bytes.Length;
                cps.Add(cps[^1] + (uint)piece.CharCount);
            }

            BinaryPrimitives.WriteUInt32LittleEndian(word.AsSpan(0x4C), cps[^1]);    // fibRgLw[3] = ccpText

            // Clx в таблице: Prc (пустой grpprl) и Pcdt с PlcPcd.
            var plc = new List<byte>();
            foreach (var cp in cps)
            {
                plc.AddRange(BitConverter.GetBytes(cp));
            }

            foreach (var (fc, compressed) in pcds)
            {
                plc.AddRange([0, 0]);
                plc.AddRange(BitConverter.GetBytes(compressed ? (fc * 2) | 0x40000000u : fc));
                plc.AddRange([0, 0]);
            }

            var clx = new List<byte> { 0x01, 0x00, 0x00, 0x02 };
            clx.AddRange(BitConverter.GetBytes((uint)plc.Count));
            clx.AddRange(plc);
            if (corruptClx)
            {
                clx[3] = 0x07; // неизвестный вид записи Clx
            }

            var table = new byte[512];
            const int clxOffset = 16;
            clx.ToArray().CopyTo(table, clxOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(word.AsSpan(0x9A + (66 * 4)), clxOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(word.AsSpan(0x9A + (67 * 4)), (uint)clx.Count);

            return Compound(word, table, whichTable == 1 ? "1Table" : "0Table");
        }

        /// <summary>Портит FAT: сектор первого потока ссылается на <paramref name="fatEntryForFirstStreamSector"/>.</summary>
        public static byte[] Corrupt(byte[] data, uint fatEntryForFirstStreamSector)
        {
            var copy = (byte[])data.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(512 + (3 * 4)), fatEntryForFirstStreamSector);
            return copy;
        }

        // Секторы: 0 — FAT, 1 — каталог, 2 — мини-FAT, 3.. — WordDocument, далее — мини-поток корня.
        private static byte[] Compound(byte[] word, byte[] table, string tableName)
        {
            const int sector = 512;
            var wordSectors = (word.Length + sector - 1) / sector;
            var miniSectors = (table.Length + 63) / 64;
            var miniStreamBytes = miniSectors * 64;
            var rootSectors = (miniStreamBytes + sector - 1) / sector;
            var firstWord = 3;
            var firstRoot = firstWord + wordSectors;
            var total = firstRoot + rootSectors;

            var file = new byte[(total + 1) * sector];
            var header = file.AsSpan(0, sector);
            new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[0x18..], 0x003E);
            BinaryPrimitives.WriteUInt16LittleEndian(header[0x1A..], 3);
            BinaryPrimitives.WriteUInt16LittleEndian(header[0x1C..], 0xFFFE);
            BinaryPrimitives.WriteUInt16LittleEndian(header[0x1E..], 9);
            BinaryPrimitives.WriteUInt16LittleEndian(header[0x20..], 6);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x2C..], 1);      // секторов FAT
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x30..], 1);      // первый сектор каталога
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x38..], 4096);   // порог мини-потока
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x3C..], 2);      // первый сектор мини-FAT
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x40..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x44..], End);    // DIFAT-секторов нет
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x48..], 0);
            for (var i = 0; i < 109; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(header[(0x4C + (i * 4))..], i == 0 ? 0u : Free);
            }

            // FAT.
            var fat = Enumerable.Repeat(Free, sector / 4).ToArray();
            fat[0] = FatSector;
            fat[1] = End;
            fat[2] = End;
            for (var s = 0; s < wordSectors; s++)
            {
                fat[firstWord + s] = s == wordSectors - 1 ? End : (uint)(firstWord + s + 1);
            }

            for (var s = 0; s < rootSectors; s++)
            {
                fat[firstRoot + s] = s == rootSectors - 1 ? End : (uint)(firstRoot + s + 1);
            }

            WriteUInts(file, Offset(0), fat);

            // Каталог: корень → WordDocument (правый сосед — таблица).
            WriteEntry(file, Offset(1), 0, "Root Entry", 5, NoStream, NoStream, 1, (uint)firstRoot, (uint)miniStreamBytes);
            WriteEntry(file, Offset(1), 1, "WordDocument", 2, NoStream, 2, NoStream, (uint)firstWord, (uint)word.Length);
            WriteEntry(file, Offset(1), 2, tableName, 2, NoStream, NoStream, NoStream, 0, (uint)table.Length);
            WriteEntry(file, Offset(1), 3, string.Empty, 0, NoStream, NoStream, NoStream, 0, 0);

            // Мини-FAT: таблица занимает мини-секторы 0..n-1.
            var miniFat = Enumerable.Repeat(Free, sector / 4).ToArray();
            for (var s = 0; s < miniSectors; s++)
            {
                miniFat[s] = s == miniSectors - 1 ? End : (uint)(s + 1);
            }

            WriteUInts(file, Offset(2), miniFat);
            word.CopyTo(file, Offset(firstWord));
            table.CopyTo(file, Offset(firstRoot));
            return file;

            static int Offset(int s) => (s + 1) * sector;
        }

        private static void WriteUInts(byte[] file, int offset, uint[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset + (i * 4)), values[i]);
            }
        }

        private static void WriteEntry(
            byte[] file, int directoryOffset, int index, string name, byte type, uint left, uint right, uint child, uint start, uint size)
        {
            var entry = file.AsSpan(directoryOffset + (index * 128), 128);
            var nameBytes = Encoding.Unicode.GetBytes(name);
            nameBytes.CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)(name.Length == 0 ? 0 : nameBytes.Length + 2));
            entry[0x42] = type;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x44..], left);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x48..], right);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], child);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x74..], start);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0x78..], size);
        }
    }
}
