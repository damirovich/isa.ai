using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Кандидат кандидат-листа поисковой сессии (<c>media.search_candidate</c>, ТФ-ПЛ-02, ТБ-072): какое лицо
/// на каком носителе и с каким расстоянием было выдано, и в каком состоянии верификации находится.
/// </summary>
/// <remarks>
/// <see cref="FaceId"/> и <see cref="AssetId"/> — ПО ЗНАЧЕНИЮ, без FK на <c>media.face</c>/<c>media.asset</c>:
/// гарантированное удаление носителя (ТБ-064/075) снимает биометрию каскадом, но НЕ должно стирать историю
/// того, что этот кандидат когда-то выдавался и как по нему решали (ТБ-072: полный кандидат-лист — в
/// журнале). Такой кандидат остаётся как история с признаком «носитель удалён» (лицо не найдено в каталоге).
/// Гриф/подразделение — носителя-источника (денормализованы из выдачи, ТБ-070); чтение — под решёткой.
/// Статус меняется ТОЛЬКО через <c>ISearchSessionStore.RecordDecisionAsync</c> вместе с решением (ТБ-073).
/// </remarks>
public class SearchCandidate : BaseEntity, IClassified
{
    /// <summary>Сессия (каскад внутри схемы: удаление сессии снимает кандидатов и решения).</summary>
    public int SessionId { get; set; }

    /// <summary>Навигация к сессии.</summary>
    public SearchSession? Session { get; set; }

    /// <summary>Ранг в выдаче (1 — ближайший).</summary>
    public int Rank { get; set; }

    /// <summary>Лицо базы (по значению, без FK — см. описание класса).</summary>
    public int FaceId { get; set; }

    /// <summary>Носитель (по значению, без FK).</summary>
    public int AssetId { get; set; }

    /// <summary>Индекс кадра (видео); <see langword="null"/> — изображение.</summary>
    public int? FrameIndex { get; set; }

    /// <summary>Таймкод кадра, мс (видео).</summary>
    public long? FrameTimestampMs { get; set; }

    /// <summary>Косинусное расстояние на момент поиска (1 − cos).</summary>
    public double CosineDistance { get; set; }

    /// <summary>Имя вырезки лица (категория <c>media-faces</c>) на момент поиска, если была.</summary>
    public string? CropStoredFileName { get; set; }

    /// <summary>Версия модели, породившей сравниваемый шаблон (ТО-прог-11).</summary>
    public required string ModelVersion { get; set; }

    /// <summary>Гриф носителя-источника (ТБ-070). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение носителя-источника (ТБ-070). NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Состояние верификации (ТБ-073); начальное — <see cref="CandidateStatus.Candidate"/>.</summary>
    public CandidateStatus Status { get; set; } = CandidateStatus.Candidate;

    /// <summary>Фигурант, к которому привязан кандидат (значение из схемы профиля, ТФ-ВЕР-03); <see langword="null"/> — не привязан.</summary>
    public int? PersonRef { get; set; }
}
