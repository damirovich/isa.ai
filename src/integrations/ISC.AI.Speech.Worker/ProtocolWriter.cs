using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace ISC.AI.Speech.Worker;

/// <summary>
/// Запись протокола в stdout (ADR-0026): одна строка — один JSON-объект, UTF-8, перевод строки «\n».
/// <list type="bullet">
/// <item><c>{"type":"segment","index":0,"startMs":1230,"endMs":4560,"text":"..."}</c> — фрагмент речи;</item>
/// <item><c>{"type":"done","durationMs":123456,"segments":N}</c> — последняя строка успешного прогона.</item>
/// </list>
/// Разбирает протокол адаптер <c>ISC.AI.Speech</c> (<c>SpeechWorkerProtocol</c>); менять формат — только
/// согласованно с ним.
/// </summary>
/// <remarks>
/// Каждая строка сбрасывается сразу: адаптер отдаёт фрагменты по мере готовности, и многочасовая запись
/// не должна копиться в буфере процесса. Кириллица (русская и киргизская — ң, ө, ү входят в блок
/// Cyrillic) пишется как есть, чтобы вывод читался при ручной проверке; остальное экранируется.
/// Индексы фрагментов идут подряд с нуля, пустой текст не пишется (<see cref="WriteSegment"/>).
/// </remarks>
internal sealed class ProtocolWriter(TextWriter output)
{
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic, UnicodeRanges.CyrillicSupplement),
    };

    private readonly ArrayBufferWriter<byte> _buffer = new(256);

    /// <summary>Сколько фрагментов записано — это же число уходит в строку <c>done</c>.</summary>
    public int SegmentsWritten { get; private set; }

    /// <summary>
    /// Пишет фрагмент. Текст обрезается по краям; пустой не пишется (у модели бывает пустой ответ на
    /// шум, принятый детектором за речь). Возвращает <see langword="true"/>, если строка записана.
    /// </summary>
    public bool WriteSegment(long startMs, long endMs, string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        WriteLine(json =>
        {
            json.WriteString("type", "segment");
            json.WriteNumber("index", SegmentsWritten);
            json.WriteNumber("startMs", startMs);
            json.WriteNumber("endMs", endMs);
            json.WriteString("text", trimmed);
        });
        SegmentsWritten++;
        return true;
    }

    /// <summary>Пишет итоговую строку: длительность записи и число фрагментов.</summary>
    public void WriteDone(long durationMs) =>
        WriteLine(json =>
        {
            json.WriteString("type", "done");
            json.WriteNumber("durationMs", durationMs);
            json.WriteNumber("segments", SegmentsWritten);
        });

    private void WriteLine(Action<Utf8JsonWriter> body)
    {
        _buffer.ResetWrittenCount();
        using (var json = new Utf8JsonWriter(_buffer, JsonOptions))
        {
            json.WriteStartObject();
            body(json);
            json.WriteEndObject();
        }

        output.Write(Encoding.UTF8.GetString(_buffer.WrittenSpan));
        output.Write('\n');
        output.Flush();
    }
}
