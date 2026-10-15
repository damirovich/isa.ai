using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Расшифровка, текст которой есть в корпусе ядра (для поиска по смыслу и чата по материалам, ADR-0051).</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="CoreDocumentId">Документ корпуса ядра (<c>core.document.id</c>).</param>
/// <param name="OriginalFileName">Имя файла записи (для подписи источника).</param>
/// <param name="Kind">Вид носителя (аудио или видео).</param>
/// <param name="Classification">Гриф носителя.</param>
public sealed record TranscriptCorpusRef(int AssetId, int CoreDocumentId, string OriginalFileName, MediaKind Kind, short Classification);

/// <summary>Расшифровки области: проиндексированные и сколько всего готовых расшифровок видно (для строки готовности).</summary>
/// <param name="Indexed">Расшифровки с текстом в корпусе.</param>
/// <param name="Transcribed">Сколько видимых носителей области имеют готовую непустую расшифровку.</param>
public sealed record TranscriptCorpusScope(IReadOnlyList<TranscriptCorpusRef> Indexed, int Transcribed);

/// <summary>Итог индексации расшифровки в корпус.</summary>
public enum TranscriptIndexStatus
{
    /// <summary>Проиндексирована (новая версия в корпусе; прежняя, если была, уничтожена).</summary>
    Indexed,

    /// <summary>Текст не менялся — корпус уже актуален.</summary>
    Unchanged,

    /// <summary>Расшифровки нет или она пуста — в корпусе её нет (прежняя версия, если была, уничтожена).</summary>
    Empty,

    /// <summary>Носителя нет.</summary>
    NotFound,

    /// <summary>Индексация расшифровок выключена настройкой.</summary>
    Disabled,

    /// <summary>Конвейер ядра отказал (причина — в <see cref="TranscriptIndexResult.Reason"/>).</summary>
    Rejected,
}

/// <summary>Итог индексации расшифровки.</summary>
/// <param name="Status">Статус.</param>
/// <param name="CoreDocumentId">Документ корпуса при успехе.</param>
/// <param name="Reason">Причина отказа.</param>
public sealed record TranscriptIndexResult(TranscriptIndexStatus Status, int? CoreDocumentId = null, string? Reason = null);

/// <summary>
/// Расшифровки речи в корпусе ядра (ADR-0051): текст готовой расшифровки с метками времени индексируется для поиска по
/// смыслу и ответов чата по материалам; источник ответа ведёт к моменту записи. Мостик носитель ↔ документ корпуса —
/// <c>media.transcript_index_link</c>.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ. Гриф и подразделение документа корпуса — носителя (ТБ-070); выдача области — под решёткой пакета
/// (<see cref="ResolveAsync"/>), поверх неё ядро ещё раз применяет floor допуска к фрагментам (ТБ-020). Уничтожение
/// носителя уничтожает и документ корпуса (ТБ-064); новая расшифровка заменяет прежнюю: старая версия уничтожается,
/// а не остаётся «неактуальной». Индексация и дозаполнение работают от имени системы (фоновая очередь).
/// </remarks>
public interface ITranscriptCorpus
{
    /// <summary>Проиндексированные расшифровки среди <paramref name="assetIds"/>, видимых субъекту, и число готовых.</summary>
    Task<TranscriptCorpusScope> ResolveAsync(IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Проиндексировать (или переиндексировать) расшифровку носителя.</summary>
    Task<TranscriptIndexResult> IndexAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>Носители с готовой расшифровкой, которой нет в корпусе или которая новее проиндексированной.</summary>
    Task<IReadOnlyList<int>> ListPendingAsync(int limit, CancellationToken cancellationToken = default);
}

/// <summary>Текст расшифровки для корпуса и разбор меток времени в нём (ADR-0051).</summary>
public static partial class TranscriptCorpusText
{
    /// <summary>Тип документа корпуса.</summary>
    public const string DocType = "Расшифровка записи";

    /// <summary>Источник документа корпуса.</summary>
    public const string Source = "Медиа";

    /// <summary>Ключ метаданных документа корпуса — носитель.</summary>
    public const string AssetIdKey = "media_asset_id";

    /// <summary>
    /// Текст: шапка с носителем (номер делает текст уникальным — дедупликация корпуса по хешу не склеит расшифровки
    /// двух записей с одинаковыми словами) и фрагменты абзацами «[чч:мм:сс] текст» — чанкер режет по абзацам, и метка
    /// времени попадает в начало каждого фрагмента корпуса.
    /// </summary>
    public static string Build(int assetId, string originalFileName, DateTime? capturedAtUtc, IEnumerable<(long StartMs, string Text)> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Расшифровка записи «{originalFileName}» (носитель № {assetId})");
        if (capturedAtUtc is { } captured)
        {
            text.Append(CultureInfo.InvariantCulture, $", запись от {captured:dd.MM.yyyy}");
        }

        text.Append(". Автоматическая расшифровка речи — возможны ошибки распознавания.");
        foreach (var (startMs, segment) in segments)
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                text.Append("\n\n").Append(Timecode(startMs)).Append(' ').Append(segment.Trim());
            }
        }

        return text.ToString();
    }

    /// <summary>Метка времени «[чч:мм:сс]».</summary>
    public static string Timecode(long ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"[{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}]");
    }

    /// <summary>Момент первой метки времени во фрагменте, мс; <see langword="null"/> — меток нет.</summary>
    public static long? FirstTimecodeMs(string? fragment)
    {
        if (fragment is null || TimecodePattern().Match(fragment) is not { Success: true } match)
        {
            return null;
        }

        var hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return ((hours * 60L + minutes) * 60 + seconds) * 1000;
    }

    [GeneratedRegex(@"\[(\d{2,}):(\d{2}):(\d{2})\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimecodePattern();
}
