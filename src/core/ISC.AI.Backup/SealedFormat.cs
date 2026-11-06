using System.Text;

namespace ISC.AI.Backup;

/// <summary>
/// Формат зашифрованного файла (<see cref="SealedFile"/>): сигнатура в начале файла и слова, которыми о таком файле говорят
/// сообщения об ошибке. Схема шифрования у всех форматов одна; сигнатура не даёт принять файл одного назначения за другой —
/// пакет обмена не расшифруется как резервная копия и наоборот.
/// </summary>
/// <param name="Magic">Сигнатура — ровно 8 символов ASCII.</param>
/// <param name="Name">Что это за файл — в сообщении «не тот файл»: «файл резервной копии ISC.AI».</param>
/// <param name="FileNoun">Начало сообщений о повреждении: «Файл копии».</param>
/// <param name="WrongKeyLead">Начало сообщения о чужом ключе: «Копия зашифрована».</param>
public sealed record SealedFormat(string Magic, string Name, string FileNoun, string WrongKeyLead)
{
    /// <summary>Длина сигнатуры, байт.</summary>
    public const int MagicLength = 8;

    /// <summary>Файл резервной копии (ТБ-082, ADR-0063).</summary>
    public static SealedFormat Backup { get; } = new("ISCBAK01", "файл резервной копии ISC.AI", "Файл копии", "Копия зашифрована");

    /// <summary>Файл пакета обмена между офисами (ТБ-083, ADR-0068).</summary>
    public static SealedFormat Exchange { get; } = new("ISCXCH01", "файл пакета обмена ISC.AI", "Файл пакета", "Пакет зашифрован");

    /// <summary>Сигнатура байтами.</summary>
    /// <exception cref="InvalidOperationException">Сигнатура не из 8 символов ASCII — ошибка программиста.</exception>
    public byte[] MagicBytes()
    {
        var bytes = Encoding.ASCII.GetBytes(Magic);
        return bytes.Length == MagicLength && Encoding.ASCII.GetString(bytes) == Magic
            ? bytes
            : throw new InvalidOperationException($"Сигнатура формата «{Magic}» — не 8 символов ASCII.");
    }
}
