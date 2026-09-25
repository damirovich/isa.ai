using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Фрагмент расшифровки речи носителя (<c>media.transcript_segment</c>, ADR-0026): участок речи между
/// паузами (границы детектора речи) и его текст ДОСЛОВНО, как его выдала модель. Это ПЕРВИЧНЫЙ слой:
/// любая последующая обработка (пунктуация, нормализация) — отдельный слой и этот текст не заменяет.
/// </summary>
/// <remarks>
/// РЕЖИМ. Гриф и подразделение ДЕНОРМАЛИЗОВАНЫ с носителя (ТБ-020), как у лиц и шаблонов: любая выборка
/// фрагментов — чтение карточки или поиск по словам — фильтруется решёткой по самой строке на стороне БД
/// (ТБ-020/021), без джойна через носитель. Физически удаляемая строка (не <c>ISoftDeletable</c>): снимается
/// каскадом FK вместе с носителем — уничтожение носителя или дела уничтожает и расшифровку (ТБ-064, ADR-0025).
/// </remarks>
public class TranscriptSegment : BaseEntity, IClassified
{
    /// <summary>Носитель (аудио или видео).</summary>
    public int AssetId { get; set; }

    /// <summary>Навигация к носителю.</summary>
    public MediaAsset? Asset { get; set; }

    /// <summary>Порядковый номер фрагмента в записи (с нуля; уникален в пределах носителя).</summary>
    public int Index { get; set; }

    /// <summary>Начало фрагмента, мс от начала записи.</summary>
    public long StartMs { get; set; }

    /// <summary>Конец фрагмента, мс от начала записи.</summary>
    public long EndMs { get; set; }

    /// <summary>Текст фрагмента дословно (строчные буквы, без пунктуации — так выдаёт модель).</summary>
    public required string Text { get; set; }

    /// <summary>
    /// Модель, которой сделан фрагмент (с пином файлов): результаты разных моделей не сравнимы, и любой текст
    /// должен относиться к конкретной модели (воспроизводимость, акт).
    /// </summary>
    public required string ModelVersion { get; set; }

    /// <summary>Гриф (денормализован с носителя, ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано с носителя, ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }
}
