using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Verification;

/// <summary>
/// Элемент очереди верификации / карточка пары «проба ↔ кандидат» (ТФ-ВЕР-01/02). СЛЕПАЯ ПРОЕКЦИЯ
/// (ТБ-073, ТФ-ВЕР-02): НЕТ чужих решений и НЕТ привязки к фигуранту — верификатор судит по изображениям,
/// а не по выводу эксперта. Только собственное решение субъекта, если оно уже есть.
/// </summary>
/// <param name="CandidateId">Кандидат.</param>
/// <param name="SessionId">Поисковая сессия.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="Rank">Ранг в кандидат-листе.</param>
/// <param name="FaceId">Лицо-кандидат.</param>
/// <param name="AssetId">Носитель кандидата.</param>
/// <param name="FrameIndex">Кадр (видео).</param>
/// <param name="FrameTimestampMs">Таймкод кадра, мс (видео).</param>
/// <param name="Similarity">Косинусная схожесть — показывается только с предупреждением о вероятностной природе (ТЭ-006).</param>
/// <param name="CropStoredFileName">Вырезка лица-кандидата (категория вырезок, подкаталог — носитель).</param>
/// <param name="ProbeCropStoredFileName">
/// Вырезка пробы-ИЗОБРАЖЕНИЯ (только категория <c>media-probes</c>, подкаталог — дело сессии; маршрут раздачи —
/// по идентификатору сессии). Для пробы-лица носителя — <see langword="null"/>: вырезка берётся по <paramref name="ProbeFaceId"/>.
/// </param>
/// <param name="ProbeSha256">SHA-256 пробы (ТБ-077: идентичность пробы без хранения вектора).</param>
/// <param name="ProbeFaceId">Лицо-проба, если проба — лицо носителя (тогда вырезка — в категории вырезок его носителя).</param>
/// <param name="Classification">Гриф (показывается обязательно, ТБ-073).</param>
/// <param name="DivisionId">Подразделение.</param>
/// <param name="Status">Статус кандидата.</param>
/// <param name="OwnDecision">Решение ТЕКУЩЕГО субъекта по кандидату, если есть; чужие решения не выдаются.</param>
/// <param name="Origin">Происхождение сессии: поиск сотрудника или предложение системы (ТФ-ПЕР-09) — видно обеим стадиям.</param>
/// <param name="SuggestedPersonRef">
/// Фигурант, которого предложила система, — ТОЛЬКО на стадии эксперта (подсказка к выбору фигуранта, ТФ-ВЕР-03).
/// Верификатору не выдаётся: его решение — по изображениям, без «кто это» (ТФ-ВЕР-02). Руководителю — выдаётся.
/// </param>
/// <param name="Material">
/// Когда, откуда и где снят материал (ТФ-ПЛ-02) — сведения о материале, а не о решениях, видны всем стадиям; заполняет
/// обработчик (<see cref="IMaterialContextReader"/>), <see langword="null"/> — носитель вне допуска.
/// </param>
public sealed record VerificationQueueItem(
    int CandidateId,
    int SessionId,
    int CaseId,
    int Rank,
    int FaceId,
    int AssetId,
    int? FrameIndex,
    long? FrameTimestampMs,
    double Similarity,
    string? CropStoredFileName,
    string? ProbeCropStoredFileName,
    string ProbeSha256,
    int? ProbeFaceId,
    short Classification,
    int DivisionId,
    CandidateStatus Status,
    VerificationDecision? OwnDecision,
    SessionOrigin Origin = SessionOrigin.Operator,
    int? SuggestedPersonRef = null,
    MaterialContext? Material = null)
{
    /// <summary>
    /// Слепая проекция полной строки кандидата: из решений остаётся только решение <paramref name="userId"/>,
    /// привязка к фигуранту (<c>PersonRef</c>) отбрасывается (ТФ-ВЕР-02). Предложенный системой фигурант остаётся только
    /// для стадий эксперта и руководителя (<paramref name="stage"/>); по умолчанию — стадия верификатора, то есть «слепо».
    /// Решения сотрудников руководитель получает ОТДЕЛЬНЫМ запросом (<see cref="GetSupervisorReviewQuery"/>): этот тип
    /// структурно слеп, чтобы ни одна стадия не получила чужие решения через общую проекцию.
    /// </summary>
    public static VerificationQueueItem From(
        SearchCandidateRow candidate, SearchSessionRow session, int userId, VerificationStage stage = VerificationStage.Verifier)
    {
        System.ArgumentNullException.ThrowIfNull(candidate);
        System.ArgumentNullException.ThrowIfNull(session);

        return new VerificationQueueItem(
            candidate.Id,
            candidate.SessionId,
            candidate.CaseId,
            candidate.Rank,
            candidate.FaceId,
            candidate.AssetId,
            candidate.FrameIndex,
            candidate.FrameTimestampMs,
            candidate.Similarity,
            candidate.CropStoredFileName,
            session.ProbeCropStoredFileName,
            session.ProbeSha256,
            session.ProbeFaceId,
            candidate.Classification,
            candidate.DivisionId,
            candidate.Status,
            candidate.Decisions.LastOrDefault(d => d.SubjectId == userId),
            session.Origin,
            stage is VerificationStage.Expert or VerificationStage.Supervisor ? session.SuggestedPersonRef : null);
    }
}
