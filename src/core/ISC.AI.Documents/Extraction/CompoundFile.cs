using System.Buffers.Binary;
using System.Text;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Чтение потоков составного файла OLE (Compound File Binary, спецификация [MS-CFB]) — контейнера старых форматов
/// Office (<c>.doc</c>, <c>.xls</c>). Только чтение и только то, что нужно извлечению текста: потоки корневого
/// хранилища по имени (ADR-0042).
/// </summary>
/// <remarks>
/// Файл приходит от недоверенного производителя (архив Заказчика), поэтому каждая ссылка проверяется: номер сектора —
/// в пределах файла, цепочка секторов — без циклов и не длиннее файла, размер потока — не больше файла. Нарушение —
/// <see cref="InvalidDataException"/>, а не зависание или выход за массив.
/// </remarks>
internal sealed class CompoundFile
{
    /// <summary>Сигнатура составного файла OLE.</summary>
    public static ReadOnlySpan<byte> Signature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const int DirectoryEntrySize = 128;
    private const uint NoStream = 0xFFFFFFFF;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly bool _version3;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<DirectoryEntry> _entries;
    private readonly byte[] _miniStream;

    private CompoundFile(byte[] data)
    {
        _data = data;
        if (data.Length < 512 || !data.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("Файл не является составным файлом OLE.");
        }

        var major = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1A));
        _version3 = major == 3;
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1E));
        var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x20));
        if (sectorShift is not (9 or 12) || miniShift != 6)
        {
            throw new InvalidDataException("Неподдерживаемый размер сектора составного файла.");
        }

        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        _miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x38));

        var fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x2C));
        var firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x30));
        var firstMiniFat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x3C));
        var firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x44));
        var difatCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x48));

        _fat = ReadFat(fatSectorCount, firstDifat, difatCount);
        _miniFat = firstMiniFat is EndOfChain or FreeSector ? [] : ToUInts(ReadChain(firstMiniFat, long.MaxValue));
        _entries = ReadDirectory(firstDirectory);

        var root = _entries.Count > 0 ? _entries[0] : throw new InvalidDataException("В составном файле нет корневого хранилища.");
        if (root.Size > (ulong)data.Length)
        {
            throw new InvalidDataException("Мини-поток составного файла больше самого файла.");
        }

        _miniStream = root.StartSector is EndOfChain or FreeSector ? [] : ReadChain(root.StartSector, (long)root.Size);
    }

    /// <summary>Открывает составной файл из байтов.</summary>
    public static CompoundFile Open(byte[] data) => new(data ?? throw new ArgumentNullException(nameof(data)));

    /// <summary>Файл начинается сигнатурой составного файла OLE.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    /// <summary>
    /// Содержимое потока корневого хранилища по имени (без учёта регистра) или <see langword="null"/>, если его нет.
    /// Потоки вложенных хранилищ (например, внедрённых объектов) не рассматриваются.
    /// </summary>
    public byte[]? ReadRootStream(string name)
    {
        foreach (var index in RootChildren())
        {
            var entry = _entries[index];
            if (entry.Type == 2 && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return ReadStream(entry);
            }
        }

        return null;
    }

    private byte[] ReadStream(DirectoryEntry entry)
    {
        if (entry.Size > (ulong)_data.Length)
        {
            throw new InvalidDataException("Поток составного файла больше самого файла.");
        }

        if (entry.Size < _miniStreamCutoff)
        {
            return ReadMiniChain(entry.StartSector, (long)entry.Size);
        }

        return ReadChain(entry.StartSector, (long)entry.Size);
    }

    // Обход красно-чёрного дерева детей корня (левый/правый сосед) без рекурсии, с защитой от циклов.
    private List<int> RootChildren()
    {
        var result = new List<int>();
        var start = _entries[0].Child;
        if (start == NoStream)
        {
            return result;
        }

        var stack = new Stack<uint>();
        var seen = new HashSet<uint>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (id == NoStream || id >= _entries.Count || !seen.Add(id))
            {
                continue;
            }

            result.Add((int)id);
            stack.Push(_entries[(int)id].Left);
            stack.Push(_entries[(int)id].Right);
        }

        return result;
    }

    private uint[] ReadFat(uint fatSectorCount, uint firstDifat, uint difatCount)
    {
        var maxSectors = (uint)(_data.Length / _sectorSize);
        if (fatSectorCount > maxSectors)
        {
            throw new InvalidDataException("Число секторов FAT больше размера файла.");
        }

        var fatSectors = new List<uint>();
        for (var i = 0; i < 109 && fatSectors.Count < fatSectorCount; i++)
        {
            fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(0x4C + (i * 4))));
        }

        // Остальные секторы FAT перечислены в цепочке секторов DIFAT: в каждом — (размер/4 − 1) номеров и следующий.
        var difat = firstDifat;
        var perSector = (_sectorSize / 4) - 1;
        for (var d = 0; d < difatCount && difat is not (EndOfChain or FreeSector) && fatSectors.Count < fatSectorCount; d++)
        {
            var offset = SectorOffset(difat);
            for (var i = 0; i < perSector && fatSectors.Count < fatSectorCount; i++)
            {
                fatSectors.Add(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + (i * 4))));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + (perSector * 4)));
        }

        var fat = new uint[fatSectors.Count * (_sectorSize / 4)];
        for (var s = 0; s < fatSectors.Count; s++)
        {
            var offset = SectorOffset(fatSectors[s]);
            for (var i = 0; i < _sectorSize / 4; i++)
            {
                fat[(s * (_sectorSize / 4)) + i] = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + (i * 4)));
            }
        }

        return fat;
    }

    private List<DirectoryEntry> ReadDirectory(uint firstSector)
    {
        var raw = ReadChain(firstSector, long.MaxValue);
        var entries = new List<DirectoryEntry>(raw.Length / DirectoryEntrySize);
        for (var offset = 0; offset + DirectoryEntrySize <= raw.Length; offset += DirectoryEntrySize)
        {
            var span = raw.AsSpan(offset, DirectoryEntrySize);
            var nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(span[0x40..]), 0, 64);
            var name = nameLength >= 2 ? Encoding.Unicode.GetString(span[..(nameLength - 2)]) : string.Empty;
            var size = BinaryPrimitives.ReadUInt64LittleEndian(span[0x78..]);
            entries.Add(new DirectoryEntry(
                name,
                span[0x42],
                BinaryPrimitives.ReadUInt32LittleEndian(span[0x44..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[0x48..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[0x4C..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[0x74..]),
                _version3 ? size & 0xFFFFFFFF : size));
        }

        return entries;
    }

    // Цепочка обычных секторов по FAT. size = long.MaxValue — читать до конца цепочки.
    private byte[] ReadChain(uint start, long size)
    {
        var output = new MemoryStream();
        var sector = start;
        var maxSteps = (_data.Length / _sectorSize) + 1;
        var steps = 0;
        var visited = new HashSet<uint>();
        while (sector is not (EndOfChain or FreeSector) && output.Length < size)
        {
            // Цикл в цепочке (сектор ссылается на уже пройденный) — повреждение, а не повтор данных.
            if (++steps > maxSteps || sector >= _fat.Length || !visited.Add(sector))
            {
                throw new InvalidDataException("Повреждённая цепочка секторов составного файла.");
            }

            var offset = SectorOffset(sector);
            var take = (int)Math.Min(_sectorSize, size - output.Length);
            output.Write(_data, offset, take);
            sector = _fat[sector];
        }

        if (size != long.MaxValue && output.Length < size)
        {
            throw new InvalidDataException("Поток составного файла обрывается раньше заявленной длины.");
        }

        return output.ToArray();
    }

    // Цепочка мини-секторов (64 байта) внутри мини-потока корня по мини-FAT.
    private byte[] ReadMiniChain(uint start, long size)
    {
        var output = new MemoryStream();
        var sector = start;
        var maxSteps = (_miniStream.Length / _miniSectorSize) + 1;
        var steps = 0;
        var visited = new HashSet<uint>();
        while (sector is not (EndOfChain or FreeSector) && output.Length < size)
        {
            var offset = (long)sector * _miniSectorSize;
            if (++steps > maxSteps || sector >= _miniFat.Length || offset + _miniSectorSize > _miniStream.Length || !visited.Add(sector))
            {
                throw new InvalidDataException("Повреждённая цепочка мини-секторов составного файла.");
            }

            var take = (int)Math.Min(_miniSectorSize, size - output.Length);
            output.Write(_miniStream, (int)offset, take);
            sector = _miniFat[sector];
        }

        if (output.Length < size)
        {
            throw new InvalidDataException("Поток составного файла обрывается раньше заявленной длины.");
        }

        return output.ToArray();
    }

    private int SectorOffset(uint sector)
    {
        var offset = ((long)sector + 1) * _sectorSize;
        if (sector >= 0xFFFFFFFA || offset + _sectorSize > _data.Length)
        {
            throw new InvalidDataException("Номер сектора составного файла за пределами файла.");
        }

        return (int)offset;
    }

    private static uint[] ToUInts(byte[] raw)
    {
        var result = new uint[raw.Length / 4];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(i * 4));
        }

        return result;
    }

    private sealed record DirectoryEntry(string Name, byte Type, uint Left, uint Right, uint Child, uint StartSector, ulong Size);
}
