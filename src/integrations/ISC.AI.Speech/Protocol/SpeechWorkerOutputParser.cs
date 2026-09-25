using System.Globalization;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Speech.Protocol;

/// <summary>
/// Сборка расшифровки из потока строк процесса-распознавателя (ADR-0026): разбор каждой строки
/// (<see cref="SpeechWorkerProtocol.ParseLine"/>) плюс проверки ЦЕЛОГО вывода — номера фрагментов идут
/// подряд, итоговая строка <c>done</c> есть, последняя и сходится по числу фрагментов. Без процесса и
/// диска — проверяется юнит-тестами.
/// </summary>
/// <remarks>
/// ЗАЧЕМ ПРОВЕРЯТЬ ЦЕЛОСТНОСТЬ ВЫВОДА. Процесс может упасть посреди записи (нехватка памяти, сбой
/// нативной библиотеки). Без строки <c>done</c> и сверки числа фрагментов оборванная расшифровка
/// сохранилась бы как законченная — и поиск по делу молча не нашёл бы слов из потерянного хвоста записи.
/// Пустой текст фрагмента пропускается (модель иногда отвечает пустотой на шум); номера отданных
/// фрагментов остаются сплошными.
/// </remarks>
public sealed class SpeechWorkerOutputParser
{
    private int _segmentLines;
    private int _accepted;

    /// <summary>Итог прогона; <see langword="null"/>, пока строка <c>done</c> не получена.</summary>
    public SpeechWorkerDone? Done { get; private set; }

    /// <summary>Сколько строк обработано (для номера строки в сообщении об ошибке).</summary>
    public int LinesRead { get; private set; }

    /// <summary>
    /// Принимает очередную строку stdout. Возвращает фрагмент для выдачи или <see langword="null"/>
    /// (пустая строка, фрагмент без текста, итоговая строка).
    /// </summary>
    /// <exception cref="FormatException">Строка не по протоколу или нарушен порядок вывода (без текста строки).</exception>
    public TranscriptSegmentDraft? Accept(string? line)
    {
        LinesRead++;

        SpeechWorkerMessage? message;
        try
        {
            message = SpeechWorkerProtocol.ParseLine(line);
        }
        catch (FormatException exception)
        {
            throw new FormatException(Prefix() + exception.Message, exception);
        }

        if (message is null)
        {
            return null;
        }

        if (Done is not null)
        {
            throw new FormatException(Prefix() + "вывод после итоговой строки done.");
        }

        switch (message)
        {
            case SpeechWorkerSegment segment:
                if (segment.Index != _segmentLines)
                {
                    throw new FormatException(Prefix() + string.Create(CultureInfo.InvariantCulture,
                        $"ожидался фрагмент № {_segmentLines}, получен № {segment.Index} — фрагменты потеряны или повторены."));
                }

                _segmentLines++;
                var text = segment.Text.Trim();
                return text.Length == 0
                    ? null
                    : new TranscriptSegmentDraft(_accepted++, segment.StartMs, segment.EndMs, text);

            case SpeechWorkerDone done:
                if (done.Segments != _segmentLines)
                {
                    throw new FormatException(Prefix() + string.Create(CultureInfo.InvariantCulture,
                        $"итог сообщает о {done.Segments} фрагм., а получено {_segmentLines}."));
                }

                Done = done;
                return null;

            default:
                throw new FormatException(Prefix() + "неизвестная строка протокола.");
        }
    }

    /// <summary>Проверяет, что вывод завершён итоговой строкой (вызывается после конца stdout).</summary>
    /// <exception cref="FormatException">Итоговой строки нет — вывод оборван.</exception>
    public void EnsureCompleted()
    {
        if (Done is null)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                $"вывод оборван: нет итоговой строки done (получено фрагментов: {_segmentLines})."));
        }
    }

    private string Prefix() => string.Create(CultureInfo.InvariantCulture, $"строка {LinesRead}: ");
}
