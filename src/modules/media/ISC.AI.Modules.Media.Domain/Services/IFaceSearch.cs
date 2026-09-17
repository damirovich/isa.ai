using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт поиска «лицо по фото» (ТС-012): ближайшие шаблоны к вектору-пробе с РЕШЁТКОЙ ДОСТУПА НА
/// СТОРОНЕ БД (ТБ-020/070, GATE-4). Реализация — <c>Media.Data</c> (pgvector + HNSW).
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (те же, что у ретривера ядра, GATE-1): (1) без <see cref="AccessContext"/> — исключение
/// <see cref="AccessContextRequiredException"/>, а не поиск без фильтра (ТБ-021); (2) floor ядра
/// <see cref="BaselineAccess"/> применяется ВСЕГДА, политика профиля — только поверх и только сужая;
/// (3) шаблоны вне допуска не участвуют даже в ранжировании — pre-filter в SQL до ANN-обхода;
/// (4) пустая выдача при отсутствии допуска неотличима от «лица нет в базе» (по контенту).
/// Поиск — режимное действие: вызывающий сценарий обязан записать аудит (ТБ-072).
/// </remarks>
public interface IFaceSearch
{
    /// <summary>Возвращает до <c>TopK</c> ближайших кандидатов, доступных субъекту, по возрастанию расстояния.</summary>
    Task<IReadOnlyList<FaceCandidate>> SearchAsync(
        FaceSearchQuery query, AccessContext access, CancellationToken cancellationToken = default);
}
