using System.Globalization;
using System.Text.Json;

namespace ISC.AI.Speech.Protocol;

/// <summary>Строка протокола процесса-распознавателя (ADR-0026).</summary>
public abstract record SpeechWorkerMessage;

/// <summary>Фрагмент речи: <c>{"type":"segment","index":0,"startMs":1230,"endMs":4560,"text":"..."}</c>.</summary>
/// <param name="Index">Номер фрагмента в выводе, с нуля, подряд.</param>
/// <param name="StartMs">Начало, мс от начала записи.</param>
/// <param name="EndMs">Конец, мс от начала записи.</param>
/// <param name="Text">Дословный текст модели (может быть пустым — такой фрагмент пропускается).</param>
public sealed record SpeechWorkerSegment(int Index, long StartMs, long EndMs, string Text) : SpeechWorkerMessage;

/// <summary>Итог успешного прогона: <c>{"type":"done","durationMs":123456,"segments":N}</c>.</summary>
/// <param name="DurationMs">Длительность записи, мс.</param>
/// <param name="Segments">Сколько строк-фрагментов выведено до итога.</param>
public sealed record SpeechWorkerDone(long DurationMs, int Segments) : SpeechWorkerMessage;

/// <summary>
/// Разбор протокола процесса-распознавателя <c>ISC.AI.Speech.Worker</c> (ADR-0026): одна строка stdout —
/// один JSON-объект. Чистая функция без процесса и диска — проверяется юнит-тестами.
/// </summary>
/// <remarks>
/// ТЕКСТ СТРОКИ НЕ ПОПАДАЕТ В СООБЩЕНИЕ ОБ ОШИБКЕ. В строке может быть расшифровка — то есть содержимое
/// материала дела с грифом; сообщение же уходит в статус носителя, журнал и логи. Поэтому ошибка называет
/// только причину и позицию (ТД-007, ТБ-020).
/// </remarks>
public static class SpeechWorkerProtocol
{
    /// <summary>Значение поля <c>type</c> строки-фрагмента.</summary>
    public const string SegmentType = "segment";

    /// <summary>Значение поля <c>type</c> итоговой строки.</summary>
    public const string DoneType = "done";

    /// <summary>
    /// Разбирает строку. Пустая строка (или из одних пробелов) — <see langword="null"/>: переводы строк
    /// не считаются нарушением протокола.
    /// </summary>
    /// <exception cref="FormatException">Строка — не JSON-объект протокола: причина в тексте, содержимого строки нет.</exception>
    public static SpeechWorkerMessage? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException exception)
        {
            // Позиция ошибки — из исключения (строка и байт), без текста строки.
            throw new FormatException(
                $"строка не является JSON (строка {exception.LineNumber}, позиция {exception.BytePositionInLine}).", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"ожидался JSON-объект, получено: {root.ValueKind}.");
            }

            var type = ReadString(root, "type");
            switch (type)
            {
                case SegmentType:
                {
                    var index = ReadInt32(root, "index");
                    var start = ReadInt64(root, "startMs");
                    var end = ReadInt64(root, "endMs");
                    var text = ReadString(root, "text");
                    if (index < 0 || start < 0 || end < start)
                    {
                        throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                            $"фрагмент с неверными номером или границами: index={index}, startMs={start}, endMs={end}."));
                    }

                    return new SpeechWorkerSegment(index, start, end, text);
                }

                case DoneType:
                {
                    var duration = ReadInt64(root, "durationMs");
                    var segments = ReadInt32(root, "segments");
                    if (duration < 0 || segments < 0)
                    {
                        throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                            $"итоговая строка с отрицательными значениями: durationMs={duration}, segments={segments}."));
                    }

                    return new SpeechWorkerDone(duration, segments);
                }

                default:
                    throw new FormatException($"неизвестный тип строки «{Shorten(type)}».");
            }
        }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"нет строкового поля «{name}».");

    private static int ReadInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new FormatException($"нет целочисленного поля «{name}».");

    private static long ReadInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw new FormatException($"нет целочисленного поля «{name}».");

    // Тип строки — служебное поле, но и его не тащим в сообщение целиком.
    private static string Shorten(string value) => value.Length <= 32 ? value : value[..32] + "…";
}
